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
        Basis = DomainInputs.workBasis
    }

let private account =
    { DomainInputs.actorReference (CaseFixtures.actorId CliReplyFixtures.id) with
        DisplayName = DisplayName.Named(DomainInputs.name "Tess")
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
                            (Amount.parse "100" |> CaseBuilders.accepted)
                            (Currency.parse "EUR" |> CaseBuilders.accepted)
                    ),
                    [ [ "100.00 EUR" ]; [ "100,00 EUR" ]; [ "100.00 EUR" ] ]
                    PresentedValue.moneyTotal
                        (Currency.parse "USD" |> CaseBuilders.accepted)
                        (DecimalTotal.parse "0" |> CaseBuilders.accepted),
                    [ [ "0.00 USD" ]; [ "0,00 USD" ]; [ "0.00 USD" ] ]
                    PresentedValue.Date(DomainInputs.date "2026-11-04"),
                    [ [ "4 Nov 2026" ]; [ "4 nov. 2026" ]; [ "4 نوفمبر 2026" ] ]
                    PresentedValue.Instant(DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)),
                    [
                        [ "4 Nov 2026 12:00 +00:00" ]
                        [ "4 nov. 2026 12:00 +00:00" ]
                        [ "4 نوفمبر 2026 12:00\u00a0م +00:00"; "4 نوفمبر 2026 12:00 م +00:00" ]
                    ]
                    PresentedValue.Party(DomainInputs.name "Tess"), List.replicate 3 [ "Tess" ]
                    PresentedValue.Text "Text", List.replicate 3 [ "Text" ]
                    PresentedValue.Narrative "First\nSecond", List.replicate 3 [ "First\nSecond" ]
                    PresentedValue.Message "label.recorded",
                    [ [ "Recorded." ]; [ "Reģistrēts." ]; [ "تم التسجيل." ] ]
                    PresentedValue.Code("boolean", "TRUE"), [ [ "Yes" ]; [ "Jā" ]; [ "نعم" ] ]
                    PresentedValue.Codes("boolean", [ "FALSE"; "TRUE" ]),
                    [ [ "No, Yes" ]; [ "Nē, Jā" ]; [ "لا, نعم" ] ]
                    PresentedValue.Actor account, List.replicate 3 [ "Tess" ]
                    PresentedValue.State(CaseHeadline.Erased, None),
                    [ [ "Erased" ]; [ "Dzēsta" ]; [ "تم المحو" ] ]
                    PresentedValue.Count 42L, List.replicate 3 [ "42" ]
                    PresentedValue.Reference(DomainInputs.reference "R-1"),
                    List.replicate 3 [ "R-1" ]
                    PresentedValue.Identifier "ID-1", List.replicate 3 [ "ID-1" ]
                    PresentedValue.Absent "label.notRecorded",
                    [ [ "Not recorded" ]; [ "Nav ierakstīts" ]; [ "غير مسجّل" ] ]
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
                    Expect.contains
                        expected
                        (rendered (RenderingContext.forLanguage language) value)
                        "Human format keeps exact language goldens, including the observed Arabic day-period separator variants")

[<Tests>]
let reviews =
    testList
        "typed operation review deltas"
        [
            testCase "work summary reviews omit unchanged responsibility" (fun () ->
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
                    Reviews.workReview
                        (TaskId.ofLiteral "work.update")
                        (Binding.Work(CaseSelector.Id before.CaseId, before.Revision))
                        CliReplyFixtures.snapshot
                        before
                        proposed
                        context

                let delta = ReviewScreen.operationReview Map.empty review

                Expect.equal
                    (delta.Changes |> List.map _.Key)
                    [ CaseId.format before.CaseId + ".summary" ]
                    "The core supplies only actual changes"

                Expect.isEmpty delta.Unchanged "Unchanged responsibility stays out of the review"

                let text =
                    OperationText.previewLines
                        (RenderingContext.forLanguage "en")
                        None
                        (RepresentationReply.Human(
                            HumanReplies.previewSubmission
                                (Submissions.create
                                    "work.update"
                                    CliReplyFixtures.id
                                    (Binding.Work(CaseSelector.Id before.CaseId, Revision.first))
                                    (Submissions.fields
                                        [ BusinessField.HandlingSummary, "After\nSecond line" ]))
                                {
                                    PreviewReply.OperationId = CliReplyFixtures.id
                                    Outcome = Outcome.Succeeded(Previewed.Reviewed review)
                                }
                        ))
                    |> String.concat "\n"

                Expect.stringContains
                    text
                    "Before → After\nSecond line"
                    "Admitted narrative layout is preserved"

                Expect.equal
                    (text.Split('\n')
                     |> Array.filter (fun (line: string) ->
                         line.StartsWith("Unchanged:", StringComparison.Ordinal))
                     |> Array.length)
                    0
                    "No unchanged summary line"

                Expect.isFalse
                    (text.Contains "→ Tess" || text.Contains "Tess →")
                    "Unchanged person does not become a fake change")
            testCase "case money changes are atomic and erasure removes retained fields" (fun () ->
                let input =
                    DomainInputs.registrationWith
                        [ BusinessField.ClaimedAmount, "0"; BusinessField.ClaimedCurrency, "EUR" ]
                        DomainInputs.registration

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
                                            DomainInputs.registrationWith
                                                [ BusinessField.ClaimedAmount, "12.3400" ]
                                                input
                                        )
                                }
                    }

                let review proposed =
                    Reviews.caseReview
                        (TaskId.ofLiteral "case.amendRegistration")
                        (Binding.Case(CaseSelector.Id before.CaseId, before.Revision))
                        (Some before)
                        proposed
                        context

                let delta = ReviewScreen.operationReview Map.empty (review proposed)

                Expect.equal
                    (delta.Changes |> List.map _.Key)
                    [ CaseId.format before.CaseId + ".claimedAmount" ]
                    "The currency is not a competing changed field"

                Expect.equal
                    (rendered (RenderingContext.forLanguage "en") delta.Changes.Head.Before,
                     rendered (RenderingContext.forLanguage "en") delta.Changes.Head.After)
                    ("0.00 EUR", "12.34 EUR")
                    "All accepted money digits and minor units survive"

                let erased =
                    ReviewScreen.operationReview
                        Map.empty
                        (review { before with State = CaseState.Erased })

                Expect.isTrue
                    (erased.Changes
                     |> List.exists (fun change ->
                         change.Key = CaseId.format before.CaseId + ".claimedAmount"
                         && change.After = PresentedValue.Absent "lifecycle.ERASED.label"))
                    "Erasure is a real removal, even with an empty historical scalar changes list"

                Expect.isFalse
                    (erased.Unchanged |> List.exists (fun entry -> entry.Key = "claimedAmount"))
                    "Removed money never becomes unchanged")
            testCase
                "plain text still escapes LF while admitted narratives retain paragraphs"
                (fun () ->
                    let plain =
                        rendered
                            (RenderingContext.forLanguage "en")
                            (PresentedValue.Text "First\nSecond")

                    Expect.isFalse (plain.Contains '\n') "Single-line data cannot inject layout"
                    Expect.stringContains plain "\\u{000A}" "The data escape is atomic and visible"

                    Expect.equal
                        (rendered
                            (RenderingContext.forLanguage "en")
                            (PresentedValue.Narrative "First\nSecond"))
                        "First\nSecond"
                        "Only the narrative kind admits LF layout")
            testCase
                "withheld people target is explicit and never given an erased profile name"
                (fun () ->
                    let identity = CaseFixtures.actorId CliReplyFixtures.id

                    let receipt =
                        { CliReplyFixtures.receipt with
                            RecordedBy = AuthorityRef.Actor(account)
                            Subject =
                                ReceiptSubject.People
                                    {
                                        Act = (TaskId.ofLiteral "people.disable")
                                        TargetActor = PeopleTargetDisclosure.Withheld identity
                                        Effects =
                                            [
                                                {
                                                    Act = (TaskId.ofLiteral "people.disable")
                                                    Id =
                                                        EffectId.create
                                                            CliReplyFixtures.receipt.OperationId
                                                            0
                                                        |> Option.get
                                                    Position =
                                                        EffectPosition.create
                                                            CliReplyFixtures.receipt
                                                                .Position.Sequence
                                                            0
                                                        |> Option.get
                                                }
                                            ]
                                    }
                            Basis =
                                { DomainInputs.workBasis with
                                    // November has a stable abbreviation across the supported ICU versions.
                                    // Keep observation on a different day from both business and recording dates.
                                    BusinessDate = DateOnly(2026, 10, 31)
                                    ObservedAt =
                                        DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero)
                                }
                        }

                    let output =
                        OperationText.receiptLinesWithDetails
                            true
                            (RenderingContext.forLanguage "en")
                            false
                            (HumanReceiptDocument.ofCore receipt)
                        |> String.concat "\n"

                    Expect.stringContains
                        output
                        "You do not have access to these details."
                        "Withholding is not profile erasure"

                    Expect.stringContains
                        output
                        (ActorId.format identity)
                        "Known identity remains in record details"

                    Expect.isFalse (output.Contains "Former account") "No fabricated erased actor"

                    Expect.stringContains
                        output
                        "1 Nov 2026 12:00 +00:00"
                        "The observation comes from the current receipt basis"

                    Expect.stringContains
                        output
                        "1 Jan 2026 00:00 +00:00"
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
            Reviews.workReview
                (TaskId.ofLiteral "work.update")
                (Binding.Work(CaseSelector.Id before.CaseId, before.Revision))
                CliReplyFixtures.snapshot
                before
                after
                context

        let delta = ReviewScreen.operationReview Map.empty review

        Expect.equal
            delta.Changes
            []
            "Assignment is an ActorId fact; current profile and availability metadata do not change it"

        Expect.isEmpty delta.Unchanged "The retained assignment needs no review")


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
