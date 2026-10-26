module ClaimCore.Tests.OperationCatalogTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.ContractGeneration
open ClaimCore.TestSupport

let private find words =
    OperationCatalog.all
    |> List.find (fun entry -> OperationCatalog.words entry = words)

[<Tests>]
let tests =
    testList
        "core operation catalog"
        [
            testCase "every actor-facing act occurs exactly once" (fun () ->
                let actual =
                    OperationCatalog.mutations |> List.choose _.Act |> List.map ActKinds.token

                let expected =
                    (CaseActKinds.all |> List.map CaseActKinds.token)
                    @ (AuthorityKinds.all
                       |> List.filter ((<>) AuthorityKind.ProvisionOwner)
                       |> List.map AuthorityKinds.token)

                Expect.equal
                    (Set.ofList actual)
                    (Set.ofList expected)
                    "All actor protocol acts; owner provisioning stays administration-only"

                Expect.equal actual.Length (List.distinct actual).Length "No duplicate act entry")
            testCase "actor endpoints are represented including derived preview" (fun () ->
                let expected =
                    Api.all
                    |> List.map Api.id
                    |> List.filter (fun id -> id <> Api.session.Token && id <> Api.logout.Token)
                    |> Set.ofList

                let actual =
                    Api.operationPreview.Token :: (OperationCatalog.all |> List.map _.Endpoint)
                    |> Set.ofList

                Expect.equal actual expected "Session bootstrap and logout belong to the host")
            testCase "annotation paths exist in their selected endpoint and act" (fun () ->
                for operation in OperationCatalog.all do
                    Expect.equal
                        (OperationCatalog.validateAnnotations
                            operation
                            (OperationCatalog.annotations operation))
                        (Ok())
                        "The annotation names a real selected path")
            testCase "a missing annotation path is rejected with a positive control" (fun () ->
                let original = find [ "case"; "open" ]
                let notes = OperationCatalog.annotations original

                Expect.equal
                    (OperationCatalog.validateAnnotations original notes)
                    (Ok())
                    "Positive control"

                let bad =
                    { notes with
                        Operands = [ [ "missingMember" ] ] @ notes.Operands
                    }

                Expect.equal
                    (OperationCatalog.validateAnnotations original bad)
                    (Error [ [ "missingMember" ] ])
                    "The negative control is detected")
            testCase
                "annotation validation selects the act rather than another union branch"
                (fun () ->
                    let opened = find [ "case"; "open" ]
                    let voided = find [ "case"; "void" ]
                    let path = [ "act"; "values" ]

                    let opening =
                        { OperationCatalog.annotations opened with
                            Prefill = [ path ]
                        }

                    let voiding =
                        { OperationCatalog.annotations voided with
                            Prefill = [ path ]
                        }

                    Expect.equal
                        (OperationCatalog.validateAnnotations opened opening)
                        (Ok())
                        "OPEN has values"

                    Expect.equal
                        (OperationCatalog.validateAnnotations voided voiding)
                        (Error [ path ])
                        "VOID cannot borrow OPEN's values")
            testCase
                "every entry and input has authored labels and meanings in all languages"
                (fun () ->
                    for language in LocalizationPolicy.languages do
                        use document =
                            JsonDocument.Parse(
                                File.ReadAllBytes(
                                    Path.Combine(
                                        RepositoryRoot.find (),
                                        "src/ClaimCore.Contract/Localization",
                                        language + ".json"
                                    )
                                )
                            )

                        let known =
                            document.RootElement.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

                        for entry in OperationCatalog.entries do
                            for key in
                                [
                                    yield entry.LabelId
                                    yield entry.MeaningId
                                    yield! Option.toList entry.ConfirmationId
                                    for input in entry.Inputs do
                                        yield input.LabelId
                                        yield! Option.toList input.MeaningId
                                ] do
                                Expect.isTrue (Set.contains key known) (language + ": " + key))
            testCase "synopses derive choices and flag names without exceptions" (fun () ->
                let grant = OperationCatalog.synopsis (find [ "authority"; "grant" ])
                Expect.stringContains grant "--kind human|service" "Vocabulary choices"

                Expect.stringContains
                    grant
                    "--case-reference VALUE"
                    "Flag derives from the input name"

                Expect.isFalse
                    (grant.Contains("--case VALUE", StringComparison.Ordinal))
                    "No retired flag alias"

                for entry in OperationCatalog.all do
                    Expect.stringStarts
                        (OperationCatalog.synopsis entry)
                        (String.concat " " (OperationCatalog.words entry))
                        "Derived command words")
            testCase "correction bindings retain conditional replacement semantics" (fun () ->
                let inputs, _ = OperationCatalog.derive (find [ "case"; "correct" ])

                Expect.equal
                    inputs.Length
                    17
                    "Three control inputs and the three correction groups"

                let payment =
                    inputs
                    |> List.find (fun input ->
                        input.Name = CatalogInputName.Field BusinessField.PaymentDate)

                Expect.equal
                    payment.Binding.Branches
                    [
                        {
                            Path = [ "act"; "groups"; "payment"; "kind" ]
                            Value = "REPLACE"
                        }
                    ]
                    "Payment date exists only for REPLACE")
            testCase "the published catalog contains all entries with their synopses" (fun () ->
                let value = Catalog.document
                Expect.equal value.Entries.Length 25 "Mutations and reads"

                Expect.equal
                    (Codec.encode CatalogCodecs.codec value
                     |> Codec.decode Limits.MaximumResponseBytes CatalogCodecs.codec)
                    (Ok value)
                    "Read and write preserve the derived catalog")
        ]
