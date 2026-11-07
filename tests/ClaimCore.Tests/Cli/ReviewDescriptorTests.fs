module ClaimCore.Tests.ReviewDescriptorTests

open System
open Expecto
open Microsoft.FSharp.Reflection
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.TestSupport

let private context =
    {
        ProductVersion = "fixture"
        Basis = Inputs.workBasis
    }

let private account =
    { Inputs.actorReference (CaseFixtures.actorId CliReplyFixtures.id) with
        DisplayName = DisplayName.Named(Inputs.name "Tess")
    }

let private rendered language value =
    TextDescriptorValue.render language value

[<Tests>]
let kindGoldens =
    testCase
        "every presented value kind has independent English Latvian and Arabic goldens"
        (fun () ->
            let rows =
                [
                    PresentedValue.money (
                        Money.create
                            (Amount.parse "100" |> Fixtures.accepted)
                            (Currency.parse "EUR" |> Fixtures.accepted)
                    ),
                    [ "100.00 EUR"; "100,00 EUR"; "100.00 EUR" ]
                    PresentedValue.moneyTotal
                        (Currency.parse "USD" |> Fixtures.accepted)
                        (DecimalTotal.parse "0" |> Fixtures.accepted),
                    [ "0.00 USD"; "0,00 USD"; "0.00 USD" ]
                    PresentedValue.Date(Inputs.date "2026-11-04"),
                    [ "4 Nov 2026"; "4 nov. 2026"; "4 نوفمبر 2026" ]
                    PresentedValue.Instant(DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)),
                    List.replicate 3 "2026-11-04T12:00:00.000000Z"
                    PresentedValue.Party(Inputs.name "Tess"), List.replicate 3 "Tess"
                    PresentedValue.Text "Text", List.replicate 3 "Text"
                    PresentedValue.Narrative "First\nSecond", List.replicate 3 "First\nSecond"
                    PresentedValue.Message "label.recorded",
                    [ "Recorded."; "Reģistrēts."; "تم التسجيل." ]
                    PresentedValue.Code("boolean", "TRUE"), [ "Yes"; "Jā"; "نعم" ]
                    PresentedValue.Codes("boolean", [ "FALSE"; "TRUE" ]),
                    [ "No, Yes"; "Nē, Jā"; "لا, نعم" ]
                    PresentedValue.Actor account, List.replicate 3 "Tess"
                    PresentedValue.State CaseHeadline.Erased, [ "Erased"; "Dzēsta"; "تم المحو" ]
                    PresentedValue.Count 42L, List.replicate 3 "42"
                    PresentedValue.Reference(Inputs.reference "R-1"), List.replicate 3 "R-1"
                    PresentedValue.Identifier "ID-1", List.replicate 3 "ID-1"
                    PresentedValue.Absent "label.notRecorded",
                    [ "Not recorded"; "Nav ierakstīts"; "غير مسجّل" ]
                ]

            let kinds =
                rows
                |> List.map (fun (value, _) ->
                    let kind, _ = FSharpValue.GetUnionFields(value, typeof<PresentedValue>) in
                    kind.Name)
                |> Set.ofList

            Expect.equal
                kinds
                (FSharpType.GetUnionCases typeof<PresentedValue>
                 |> Array.map _.Name
                 |> Set.ofArray)
                "No semantic value kind is untested"

            for value, expected in rows do
                for language, expected in List.zip [ "en"; "lv"; "ar" ] expected do
                    Expect.equal
                        (rendered language value)
                        expected
                        "Human format is explicitly pinned per language")

[<Tests>]
let reviews =
    testList
        "typed operation review deltas"
        [
            testCase
                "work summary changes retain responsibility in one unchanged summary"
                (fun () ->
                    let before =
                        {
                            CaseWorkReadModel.CaseId = CliReplyFixtures.snapshot.CaseId
                            Revision = Revision.first
                            Responsible = Some account
                            Summary = HandlingSummary.parse "Before" |> Result.toOption
                        }

                    let proposed =
                        { before with
                            Summary = HandlingSummary.parse "After\nSecond line" |> Result.toOption
                        }

                    let review =
                        OperationReview.Work
                            {
                                Parent = CliReplyFixtures.snapshot
                                Reference = CliReplyFixtures.snapshot.Reference
                                Before = before
                                Proposed = proposed
                                Context = context
                            }

                    let delta = Descriptor.operationReview None review

                    Expect.equal
                        (delta.Changes |> List.map _.Key)
                        [ "handlingSummary" ]
                        "The core supplies only actual changes"

                    Expect.equal
                        (delta.Unchanged |> List.map _.Key)
                        [ "responsible" ]
                        "The retained assignment is explicit"

                    let text =
                        TextOperationReply.reviewLines "en" None review |> String.concat "\n"

                    Expect.stringContains
                        text
                        "Before → After\nSecond line"
                        "Admitted narrative layout is preserved"

                    Expect.equal
                        (text.Split('\n')
                         |> Array.filter (fun (line: string) ->
                             line.StartsWith("Unchanged:", StringComparison.Ordinal))
                         |> Array.length)
                        1
                        "One summary line"

                    Expect.isFalse
                        (text.Contains "→ Tess" || text.Contains "Tess →")
                        "Unchanged person does not become a fake change")
            testCase "case money changes are atomic and erasure removes retained fields" (fun () ->
                let input =
                    Inputs.registrationWith
                        [ BusinessField.ClaimedAmount, "0"; BusinessField.ClaimedCurrency, "EUR" ]
                        Inputs.registration

                let fields = CaseState.fields CliReplyFixtures.snapshot.State |> Option.get

                let before =
                    { CliReplyFixtures.snapshot with
                        State =
                            CaseState.Active
                                { fields with
                                    Registration = CaseFixtures.registration input
                                }
                    }

                let proposed =
                    { before with
                        State =
                            CaseState.Active
                                { fields with
                                    Registration =
                                        CaseFixtures.registration (
                                            Inputs.registrationWith
                                                [ BusinessField.ClaimedAmount, "12.3400" ]
                                                input
                                        )
                                }
                    }

                let review proposed =
                    OperationReview.Case
                        {
                            Before = Some before
                            Proposed = proposed
                            Changes =
                                ClaimCore.Application.CaseChanges.review
                                    (CaseAct.Bare BareAct.Close)
                                    (Some before)
                                    proposed
                            Context = context
                        }

                let delta = Descriptor.operationReview None (review proposed)

                Expect.equal
                    (delta.Changes |> List.map _.Key)
                    [ "claimedAmount" ]
                    "The currency is not a competing changed field"

                Expect.equal
                    (rendered "en" delta.Changes.Head.Before,
                     rendered "en" delta.Changes.Head.After)
                    ("0.00 EUR", "12.34 EUR")
                    "All accepted money digits and minor units survive"

                let erased =
                    Descriptor.operationReview
                        None
                        (review { before with State = CaseState.Erased })

                Expect.isTrue
                    (erased.Changes
                     |> List.exists (fun change ->
                         change.Key = "claimedAmount"
                         && change.After = PresentedValue.Absent "lifecycle.ERASED.label"))
                    "Erasure is a real removal, even with an empty historical scalar changes list"

                Expect.isFalse
                    (erased.Unchanged |> List.exists (fun entry -> entry.Key = "claimedAmount"))
                    "Removed money never becomes unchanged")
            testCase
                "plain text still escapes LF while admitted narratives retain paragraphs"
                (fun () ->
                    let plain = rendered "en" (PresentedValue.Text "First\nSecond")
                    Expect.isFalse (plain.Contains '\n') "Single-line data cannot inject layout"
                    Expect.stringContains plain "\\u{000A}" "The data escape is atomic and visible"

                    Expect.equal
                        (rendered "en" (PresentedValue.Narrative "First\nSecond"))
                        "First\nSecond"
                        "Only the narrative kind admits LF layout")
            testCase
                "withheld authority target is explicit and never given an erased profile name"
                (fun () ->
                    let identity = CaseFixtures.actorId CliReplyFixtures.id

                    let receipt =
                        { CliReplyFixtures.receipt with
                            RecordedBy = account
                            Subject =
                                ReceiptSubject.Authority
                                    {
                                        Act = AuthorityKind.Disable
                                        TargetActor = AuthorityTargetDisclosure.Withheld identity
                                        Effects =
                                            [
                                                {
                                                    Act = AuthorityKind.Disable
                                                    Position = CliReplyFixtures.receipt.Position
                                                }
                                            ]
                                    }
                            Basis =
                                { Inputs.workBasis with
                                    ObservedAt = Inputs.workBasis.ObservedAt.AddDays 1.
                                }
                        }

                    let output =
                        TextOperationReply.receiptLines "en" false receipt |> String.concat "\n"

                    Expect.stringContains
                        output
                        "Details are not disclosed."
                        "Withholding is not profile erasure"

                    Expect.stringContains
                        output
                        (ActorId.format identity)
                        "Known identity remains in record details"

                    Expect.isFalse (output.Contains "Former account") "No fabricated erased actor"

                    Expect.stringContains
                        output
                        (TextObservation.line "en" receipt.Basis)
                        "The observation comes from the current receipt basis"

                    Expect.stringContains
                        output
                        (TextReplyFormat.instant receipt.RecordedAt)
                        "Original recording time remains separate")
        ]

[<Tests>]
let assignmentIdentity =
    testCase "a current profile label change is not a responsibility change" (fun () ->
        let before =
            {
                CaseWorkReadModel.CaseId = CliReplyFixtures.snapshot.CaseId
                Revision = Revision.first
                Responsible = Some account
                Summary = None
            }

        let after =
            { before with
                Responsible =
                    Some
                        { account with
                            DisplayName = DisplayName.Erased
                            Availability = ActorAvailability.Disabled
                        }
            }

        let review =
            OperationReview.Work
                {
                    Parent = CliReplyFixtures.snapshot
                    Reference = CliReplyFixtures.snapshot.Reference
                    Before = before
                    Proposed = after
                    Context = context
                }

        let delta = Descriptor.operationReview None review

        Expect.equal
            delta.Changes
            []
            "Assignment is an ActorId fact; current profile and availability metadata do not change it"

        Expect.contains
            (delta.Unchanged |> List.map _.Key)
            "responsible"
            "The existing assignment is retained")


[<Tests>]
let confirmationEscaping =
    testCase "typed-target prompts escape data while comparing the exact original target" (fun () ->
        let target = "REF\\u{001B}"
        let captured = Text.StringBuilder()

        let console: InteractiveConsole =
            {
                IsInteractive = true
                ReadLine = fun () -> Some target
                Write = fun bytes -> captured.Append(Text.Encoding.UTF8.GetString bytes) |> ignore
            }

        Expect.isTrue
            (CliConfirmation.exactTarget console "en" target)
            "Display escaping never rewrites target identity"

        Expect.equal
            (captured.ToString())
            "Type REF\\\\u{001B} to confirm: "
            "Literal backslashes stay distinct from escaped controls")
