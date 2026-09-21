module ClaimCore.Tests.SemanticIdentityTests

open System.Globalization
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain

let private current = SemanticContract.current
let private digest = SemanticContract.fingerprint

let private localizeInput =
    function
    | CommandInputShape.Fields fields -> CommandInputShape.Fields fields
    | CommandInputShape.CorrectionGroups groups ->
        groups
        |> List.map (fun group ->
            { group with
                Label = "Labojums"
                Meaning = "Prüfung — 記録"
            })
        |> CommandInputShape.CorrectionGroups

let private localized =
    { current with
        Fields =
            current.Fields
            |> List.map (fun field ->
                { field with
                    Label = "Lauks"
                    Meaning = "Latviešu — العربية — 日本語"
                })
        Commands =
            current.Commands
            |> List.map (fun command ->
                { command with
                    Label = "Darbība"
                    Meaning = "Beschreibung"
                    Inputs = localizeInput command.Inputs
                })
        Rules = current.Rules |> List.map (fun rule -> { rule with Meaning = "Skaidrojums" })
    }

let private textMutations: (ScalarTextConstraints -> ScalarTextConstraints) list =
    [
        (fun value ->
            { value with
                MinimumCharacters = value.MinimumCharacters + 1
            })
        (fun value ->
            { value with
                MaximumCharacters = value.MaximumCharacters + 1
            })
        (fun value ->
            { value with
                RequiresNonBlank = not value.RequiresNonBlank
            })
        (fun value ->
            { value with
                RejectsSurroundingWhitespace = not value.RejectsSurroundingWhitespace
            })
        (fun value ->
            { value with
                RejectsControlCharacters = not value.RejectsControlCharacters
            })
        (fun value ->
            { value with
                RequiresWellFormedUnicode = not value.RequiresWellFormedUnicode
            })
    ]

let private changeConstraints name change =
    { current with
        Fields =
            current.Fields
            |> List.map (fun field ->
                if field.Name <> name then
                    field
                else
                    let scalar =
                        match field.Scalar with
                        | ScalarRule.Text value -> ScalarRule.Text(change value)
                        | ScalarRule.Amount value ->
                            ScalarRule.Amount { value with Text = change value.Text }
                        | ScalarRule.Currency value ->
                            ScalarRule.Currency { value with Text = change value.Text }
                        | _ -> failwith "The test must select a text-like scalar."

                    { field with Scalar = scalar })
    }

let private wireDigests semantic =
    let projection = ContractProjection.create semantic
    ContractRenderers.cliFingerprint projection, ContractRenderers.webFingerprint projection

let private copyNeutrality () =
    Expect.equal (digest localized) (digest current) "Copy has no business authority"

    Expect.equal
        (wireDigests localized)
        (wireDigests current)
        "Copy is not an exact wire discriminator"

let private allScalarConstraints () =
    for name in [ "claimantName"; "claimedAmount"; "claimedCurrency" ] do
        for change in textMutations do
            Expect.notEqual (digest (changeConstraints name change)) (digest current) name

let private machineChanges () =
    let changed =
        [
            { current with
                RuleSetVersion = current.RuleSetVersion + 1
            }
            { current with
                MaximumPageSize = current.MaximumPageSize + 1
            }
            { current with
                RequestByteLimit = current.RequestByteLimit + 1
            }
            { current with
                CanonicalCommandFormat = current.CanonicalCommandFormat + 1
            }
            { current with
                Commands = List.tail current.Commands
            }
            { current with
                Fields = List.rev current.Fields
            }
        ]

    for contract in changed do
        Expect.notEqual (digest contract) (digest current) "Machine contract change"

let private cultures () =
    let before = CultureInfo.CurrentCulture
    let expected = digest current

    try
        for name in [ "en-US"; "lv-LV"; "tr-TR"; "ar-SA"; "fa-IR" ] do
            CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo name

            Expect.equal
                (digest current)
                (digest localized)
                "Presentation-neutral under every culture"

            Expect.equal (digest current) expected "Same identity as the original culture"
    finally
        CultureInfo.CurrentCulture <- before

let private tokenBoundaries () =
    let left =
        { current with
            Application = "a\000b"
            Scope = "c"
        }

    let right =
        { current with
            Application = "a"
            Scope = "b\000c"
        }

    Expect.notEqual (digest left) (digest right) "Length framing distinguishes tokens"

let private exposedRuleRevision () =
    use document =
        JsonDocument.Parse(
            current
            |> ContractProjection.create
            |> ContractRenderers.semantic
            |> CanonicalContract.bytes
        )

    Expect.equal
        (document.RootElement.GetProperty("ruleSetVersion").GetInt32())
        DomainRules.version
        "No hidden behavior version"

let private boundedCopy () =
    use document =
        JsonDocument.Parse(
            current
            |> ContractProjection.create
            |> ContractRenderers.semanticSchema
            |> CanonicalContract.bytes
        )

    let label =
        document.RootElement
            .GetProperty("$defs")
            .GetProperty("root")
            .GetProperty("properties")
            .GetProperty("fields")
            .GetProperty("prefixItems")
            .[0].GetProperty("properties")
            .GetProperty("label")

    Expect.equal (label.GetProperty("type").GetString()) "string" "Not a const schema"
    Expect.equal (label.GetProperty("maxLength").GetInt32()) 4096 "Bounded presentation"

let tests =
    testList
        "presentation-neutral semantic identity"
        [
            testCase
                "editorial and translated copy changes no semantic or wire identity"
                copyNeutrality
            testCase
                "every text constraint affects each text-like scalar identity"
                allScalarConstraints
            testCase
                "operational limits and explicit rule revisions remain identity-bearing"
                machineChanges
            testCase "semantic identity is invariant under host cultures" cultures
            testCase "embedded separators cannot collapse distinct token boundaries" tokenBoundaries
            testCase "discovery exposes the explicit executable-rule revision" exposedRuleRevision
            testCase "copy remains bounded without becoming a machine discriminator" boundedCopy
        ]
