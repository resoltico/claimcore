module ClaimCore.Tests.HumanOperationDocumentTests

open ClaimCore.CommandLine

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.Cli
open ClaimCore.TestSupport

let private accepted value =
    Result.defaultWith (fun error -> failtestf "%A" error) value

let private mutate name replacement =
    function
    | JsonValue.Object members ->
        JsonValue.Object(
            (name, replacement) :: (members |> List.filter (fun (key, _) -> key <> name))
        )
    | _ -> failtest "Expected an authored object"

let private containsMember name value =
    let rec walk =
        function
        | JsonValue.Object members ->
            members |> List.exists (fun (key, value) -> key = name || walk value)
        | JsonValue.Array values -> List.exists walk values
        | _ -> false

    walk value

let private peopleReview () =
    let principal =
        PrincipalKey.human "https://issuer.example/" "review-subject" |> accepted

    let actor =
        Inputs.namedActorReference
            (CaseFixtures.actorId (Guid.Parse "78280331-c97d-4be4-8031-f5dbd4a65282"))
            "Reviewed actor🪙"

    let state =
        {
            PeopleState.Profile = None
            Enabled = true
            Grants = []
            Access = []
        }

    let input =
        OperationInput.People
            {
                OperationId = OperationId.value ResponseExamples.receipt.OperationId
                Target =
                    {
                        Kind = PrincipalKind.Human
                        Issuer = "https://issuer.example/"
                        Subject = "review-subject"
                    }
                Act = PeopleAct.EraseProfile
            }

    let review =
        OperationReview.People
            {
                Basis = Inputs.workBasis
                Effects = [ PeopleReviewEffect.EraseProfile ]
                Target =
                    Some
                        {
                            Actor = actor
                            Principal = principal
                            Contact = None
                            State = AccountState.NoRoles
                            Roles = []
                            ResponsibleCases = None
                        }
                AccessScope = GrantScope.Installation
                Before = state
                After = state
            }

    input, review, actor

let private closedReceipt () =
    let receipt = ResponseExamples.receipt

    match receipt.Subject with
    | ReceiptSubject.Case value ->
        match value.Snapshot with
        | SnapshotDisclosure.Disclosed shown ->
            let revision = Revision.create 2L |> Option.get

            let state =
                shown.State
                |> LiveCase.toState
                |> CaseState.map (fun fields ->
                    { fields with
                        Status = CaseStatus.Closed
                    })
                |> LiveCase.ofState
                |> Option.get

            { receipt with
                Subject =
                    ReceiptSubject.Case
                        { value with
                            Act = CaseActKind.Close
                            Revision = revision
                            Snapshot = SnapshotDisclosure.Disclosed { shown with State = state }
                        }
            }
        | _ -> failtest "Disclosed case fixture required"
    | _ -> failtest "Case receipt fixture required"

[<Tests>]
let tests =
    testList
        "human operation documents"
        [
            testCase "receipt navigation is explicit and independent of CLI refresh" (fun () ->
                let receipt = closedReceipt ()
                let document = HumanReceiptDocument.ofCore receipt

                let id =
                    match receipt.Subject with
                    | ReceiptSubject.Case value -> value.CaseId
                    | _ -> failtest "Case required"

                Expect.equal
                    (document.Links
                     |> List.filter (fun link -> link.Relation = LinkRelation.OpenCase)
                     |> List.map _.Link)
                    [ Link.caseById id ]
                    "Known case destination"

                Expect.isEmpty
                    (document.Links
                     |> List.filter (fun link -> link.Relation = LinkRelation.FollowUp))
                    "Closing needs no work refresh"

                Expect.equal
                    document.Subject
                    (DocumentSubject.Receipt receipt.OperationId)
                    "Receipt identity")
            testCase
                "review supplies the authored operation identity only in Core Details"
                (fun () ->
                    let input, review, _ = peopleReview ()
                    let document = (HumanReviewDocument.ofCore input review).Document

                    let entries =
                        document.Screen.Regions
                        |> List.collect (fun region ->
                            match region.Content with
                            | RegionContent.Entries values ->
                                values |> List.map (fun entry -> region.Role, entry)
                            | _ -> [])

                    let matching =
                        entries |> List.filter (fun (_, entry) -> entry.Key = "operationId")

                    Expect.equal
                        (matching
                         |> List.map (fun (role, entry) -> role, entry.Prominence, entry.Value))
                        [
                            RegionRole.Details,
                            Prominence.Detail,
                            PresentedValue.Identifier(
                                (OperationInput.operationId input).ToString("D")
                            )
                        ]
                        "One technical identifier is supplied by Core, never reconstructed by a client")

            testCase
                "review carries core confirmation and bindings without full snapshots"
                (fun () ->
                    let input, review, actor = peopleReview ()
                    let result = HumanReviewDocument.ofCore input review

                    Expect.equal
                        result.Document.Subject
                        (DocumentSubject.Review(
                            ConfirmationTarget.Operation(OperationInput.operationId input),
                            Consequence.Irreversible
                        ))
                        "Core confirmation"

                    let action = List.exactlyOne result.Document.Actions

                    Expect.equal
                        action.Task
                        (TaskId.Operation(ActKind.People PeopleActKind.EraseProfile))
                        "Task"

                    Expect.equal
                        action.Binding
                        (OperationBinding.People(Some(ActorId.value actor.ActorId)))
                        "Target"

                    let wire = HumanDocumentCodecs.reviewDocument.Write result

                    for retired in [ "proposed"; "snapshot" ] do
                        Expect.isFalse
                            (containsMember retired wire)
                            ("No duplicated raw review " + retired)

                    HumanDocumentCodecs.reviewDocument.Read "$" wire |> accepted |> ignore

                    let unrelated =
                        HumanDocument.empty HumanDisclosure.None
                        |> HumanDocumentCodecs.document.Write
                        |> fun document -> mutate "document" document wire

                    Expect.isError
                        (HumanDocumentCodecs.reviewDocument.Validate "$" unrelated)
                        "A structurally valid unrelated document cannot be a review")

            testCase "review rejects a retired context sibling" (fun () ->
                let input, review, _ = peopleReview ()

                let wire =
                    HumanDocumentCodecs.document.Write
                        (HumanReviewDocument.ofCore input review).Document

                Expect.isError
                    (HumanDocumentCodecs.document.Validate
                        "$"
                        (mutate "context" (JsonValue.Object []) wire))
                    "Closed document")

            testCase "people receipt retains supplied outcome facts in its core Screen" (fun () ->
                let _, _, actor = peopleReview ()

                let receipt =
                    { ResponseExamples.peopleReceipt with
                        Subject =
                            ReceiptSubject.People
                                {
                                    TargetActor = PeopleTargetDisclosure.Disclosed actor
                                    Act = PeopleActKind.Disable
                                    Effects =
                                        [
                                            {
                                                Act = PeopleActKind.Disable
                                                Id =
                                                    EffectId.create
                                                        ResponseExamples.peopleReceipt.OperationId
                                                        0
                                                    |> Option.get
                                                Position =
                                                    EffectPosition.create
                                                        ResponseExamples.receipt.Position.Sequence
                                                        0
                                                    |> Option.get
                                            }
                                        ]
                                }
                    }

                let document = HumanReceiptDocument.ofCore receipt

                let rendered =
                    ViewText.renderSelected true "en" document.Screen |> String.concat "\n"

                Expect.stringContains
                    rendered
                    "Reviewed actor🪙"
                    "People facts cannot collapse to generic acceptance and UUID"

                Expect.stringContains
                    rendered
                    (ActorId.format actor.ActorId)
                    "Technical target remains available in DETAILS"

                let wire = HumanDocumentCodecs.document.Write document
                Expect.isFalse (containsMember "snapshot" wire) "No raw receipt snapshot"
                Expect.isFalse (containsMember "data" wire) "No raw DATA sibling"
                HumanDocumentCodecs.document.Validate "$" wire |> accepted |> ignore)

            testCase
                "compound receipt preserves both handles and distinguishes partial disclosure"
                (fun () ->
                    let parent = ResponseExamples.snapshot.CaseId

                    let first =
                        (ReminderId.create >> Option.get) (
                            Guid.Parse "c999fa84-cd4e-4cfe-b8bb-87929a18ff10"
                        )

                    let second =
                        (ReminderId.create >> Option.get) (
                            Guid.Parse "519e39cd-070b-4ce5-b1e0-a2f391fd54a1"
                        )

                    let next =
                        Reminder.ofAccepted
                            second
                            parent
                            Revision.first
                            (ReminderAction.parse "Permitted next reminder" |> accepted)
                            (Inputs.date "2026-11-10")
                            ReminderState.Pending
                        |> accepted

                    let version id snapshot =
                        {
                            ReminderVersion.ReminderId = id
                            Revision = Revision.first
                            Snapshot = snapshot
                        }

                    let receipt =
                        { ResponseExamples.receipt with
                            Subject =
                                ReceiptSubject.Reminder
                                    {
                                        CaseId = parent
                                        Reference = ResponseExamples.snapshot.Reference
                                        Act = ReminderActKind.CompleteAndPlanNext
                                        Effects =
                                            ReminderReceiptEffects.CompletedAndPlanned(
                                                version first ReminderDisclosure.Erased,
                                                version second (ReminderDisclosure.Disclosed next)
                                            )
                                    }
                        }

                    let document = HumanReceiptDocument.ofCore receipt

                    Expect.equal
                        document.Disclosure
                        HumanDisclosure.Partial
                        "Mixed compartments are neither wholly disclosed nor wholly erased"

                    Expect.equal
                        document.Subject
                        (DocumentSubject.Receipt receipt.OperationId)
                        "Receipt owns operation identity"

                    Expect.isEmpty document.Actions "Receipt is not an authoring surface"
                    let wire = HumanDocumentCodecs.document.Write document
                    HumanDocumentCodecs.document.Read "$" wire |> accepted |> ignore)

        ]


[<Tests>]
let mutationSummary =
    testCase "mutation text uses disclosed recorded fields and case status" (fun () ->
        let receipt = closedReceipt ()

        let receipt =
            match receipt.Subject with
            | ReceiptSubject.Case item ->
                let shown =
                    match item.Snapshot with
                    | SnapshotDisclosure.Disclosed shown -> shown
                    | _ -> failtest "Disclosed fixture"

                { receipt with
                    Subject =
                        ReceiptSubject.Case
                            { item with
                                Snapshot =
                                    SnapshotDisclosure.Disclosed { shown with RecordedFields = [] }
                            }
                }
            | _ -> failtest "Case fixture"

        let document = HumanReceiptDocument.ofCore receipt

        let reply: RepresentationReply<HumanOperationReply<HumanExecuteResult>, ExecuteReply> =
            RepresentationReply.Human
                {
                    OperationId = OperationId.value receipt.OperationId
                    Outcome = HumanExecuteResult.Accepted document
                }

        let text = OperationText.executeLines "en" reply |> String.concat "\n"
        Expect.stringContains text "Closed" "The accepted snapshot supplies the case status"
        Expect.isFalse (text.Contains "Status filter") "A case field is not a view filter"
        Expect.isFalse (text.Contains "Policy number:") "Unrelated saved facts are omitted"
        let full = ViewText.renderDocument false "en" document |> String.concat "\n"

        Expect.stringContains
            full
            "Policy number:"
            "The full supplied receipt remains available independently"

        Expect.equal
            (ReplyTextFormat.instantIn
                "en"
                "Europe/Riga"
                (DateTimeOffset.Parse "2026-01-01T23:30:00Z"))
            "2 Jan 2026 01:30 +02:00"
            "The business zone changes both date and clock"

        let usage =
            UsageText.render
                "en"
                [ "case"; "list" ]
                [ UsageError.InvalidRange("--limit", "0", 1, 200) ]

        Expect.stringContains usage "between 1 and 200" "Range advice states both bounds"

        let payable =
            PresentedValue.money (
                Money.create (Amount.parse "100" |> accepted) (Currency.parse "EUR" |> accepted)
            )

        let table =
            ScreenEntries.table
                (fun () -> "decision")
                (fun () -> None)
                [
                    ScreenEntries.column
                        "payable"
                        "field.payableAmount.label"
                        Prominence.Primary
                        true
                        (fun () -> payable)
                ]
                [ () ]

        let decision =
            { document with
                Screen =
                    Screen.create
                        [
                            ScreenEntries.region
                                RegionRole.Payments
                                None
                                (RegionContent.Table
                                    { table with
                                        Columns =
                                            table.Columns
                                            |> List.map (fun column ->
                                                { column with Recorded = true })
                                    })
                        ]
            }

        let decisionReply
            : RepresentationReply<HumanOperationReply<HumanExecuteResult>, ExecuteReply> =
            RepresentationReply.Human
                {
                    OperationId = OperationId.value receipt.OperationId
                    Outcome = HumanExecuteResult.Accepted decision
                }

        let decisionText =
            OperationText.executeLines "en" decisionReply |> String.concat "\n"

        Expect.stringContains
            decisionText
            "100.00 EUR"
            "A recorded decision keeps its supplied financial region"

        for disclosure, expected in
            [
                SnapshotDisclosure.Withheld, "Details: You do not have access to these details."
                SnapshotDisclosure.Erased, "Details: Erased"
            ] do
            let redacted =
                match receipt.Subject with
                | ReceiptSubject.Case item ->
                    { receipt with
                        Subject = ReceiptSubject.Case { item with Snapshot = disclosure }
                    }
                | _ -> failtest "Case fixture"

            let reply: RepresentationReply<HumanOperationReply<HumanExecuteResult>, ExecuteReply> =
                RepresentationReply.Human
                    {
                        OperationId = OperationId.value redacted.OperationId
                        Outcome = HumanExecuteResult.Accepted(HumanReceiptDocument.ofCore redacted)
                    }

            let output = OperationText.executeLines "en" reply |> String.concat "\n"

            Expect.stringContains
                output
                expected
                "Concise acceptance preserves the supplied disclosure explanation"

            Expect.isFalse
                (output.Contains "Policy number:")
                "Redaction does not reconstruct recorded facts")


[<Tests>]
let receiptStatusAndProgress =
    let fields = CaseState.fields ResponseExamples.snapshot.State |> Option.get

    let decided =
        FinancialPosition.undecided
        |> FinancialPosition.withDecision (Some(Inputs.admittedDecision Inputs.decision))
        |> accepted

    let payment =
        Inputs.paymentWith [ BusinessField.PaidAmount, "100" ] Inputs.payment
        |> Inputs.admittedPayment

    let partlyPaid =
        decided |> FinancialPosition.record (DateOnly(2026, 8, 20)) payment |> accepted

    let render act disclosure =
        let receipt =
            match ResponseExamples.receipt.Subject with
            | ReceiptSubject.Case item ->
                let disclosure =
                    match act, disclosure with
                    | CaseActKind.RecordPayment, SnapshotDisclosure.Disclosed shown ->
                        SnapshotDisclosure.Disclosed
                            { shown with
                                Payment =
                                    Some(
                                        PaymentDifference.Recorded(
                                            PaymentEntry.recorded
                                                ResponseExamples.receipt.OperationId
                                                payment
                                        )
                                    )
                            }
                    | _ -> disclosure

                { ResponseExamples.receipt with
                    Subject =
                        ReceiptSubject.Case
                            { item with
                                Act = act
                                Snapshot = disclosure
                            }
                }
            | _ -> failtest "Case receipt fixture"

        let reply: RepresentationReply<HumanOperationReply<HumanExecuteResult>, ExecuteReply> =
            RepresentationReply.Human
                {
                    OperationId = OperationId.value receipt.OperationId
                    Outcome = HumanExecuteResult.Accepted(HumanReceiptDocument.ofCore receipt)
                }

        OperationText.executeLines "en" reply |> String.concat "\n"

    let disclosed state recorded =
        SnapshotDisclosure.Disclosed
            {
                State = state
                RecordedFields = recorded
                Payment = None
            }

    testList
        "receipt status and payment progress"
        [
            for name, act, finance, progress, recorded in
                [
                    "open",
                    CaseActKind.Open,
                    FinancialPosition.undecided,
                    "Undecided",
                    BusinessField.ofGroup RecordGroup.Registration
                    "decide",
                    CaseActKind.Decide,
                    decided,
                    "Decided",
                    BusinessField.ofGroup RecordGroup.Decision
                    "payment",
                    CaseActKind.RecordPayment,
                    partlyPaid,
                    "Partly paid",
                    BusinessField.ofGroup RecordGroup.Payment
                ] do
                testCase name (fun () ->
                    let output =
                        render
                            act
                            (disclosed (LiveCase.Active { fields with Finance = finance }) recorded)

                    Expect.stringContains
                        output
                        "Case status: Open"
                        "Payment does not implicitly change the case status"

                    Expect.stringContains
                        output
                        ("Payment progress: " + progress)
                        "Payment progress has its own label"

                    Expect.isFalse
                        (output.Contains("Case status: " + progress))
                        "A payment headline is never a case status"

                    if act = CaseActKind.Decide then
                        Expect.stringContains
                            output
                            "750.00 EUR"
                            "Recorded financial fields remain visible")
            testCase "closed" (fun () ->
                let output =
                    render
                        CaseActKind.Close
                        (disclosed
                            (LiveCase.Active
                                { fields with
                                    Status = CaseStatus.Closed
                                    Finance = decided
                                })
                            [])

                Expect.stringContains
                    output
                    "Case status: Closed"
                    "Explicit closure supplies status"

                Expect.stringContains
                    output
                    "Payment progress: Decided"
                    "Closing does not rewrite payment progress")
            testCase "voided" (fun () ->
                let output = render CaseActKind.Void (disclosed (LiveCase.Voided fields) [])

                Expect.stringContains
                    output
                    "Lifecycle: Voided"
                    "Voiding is a lifecycle transition"

                Expect.stringContains
                    output
                    "Payment progress: Undecided"
                    "Retained financial progress remains separate"

                Expect.isFalse
                    (output.Contains "Case status:")
                    "Voided lifecycle is not a case status")
            for name, disclosure, explanation in
                [
                    "withheld",
                    SnapshotDisclosure.Withheld,
                    "Details: You do not have access to these details."
                    "erased", SnapshotDisclosure.Erased, "Details: Erased"
                ] do
                testCase name (fun () ->
                    let output = render CaseActKind.Open disclosure
                    Expect.stringContains output explanation "Disclosure remains explicit"

                    Expect.isFalse
                        (output.Contains "Case status:")
                        "Unavailable status is not reconstructed"

                    Expect.isFalse
                        (output.Contains "Payment progress:")
                        "Unavailable finances are not reconstructed"

                    if disclosure = SnapshotDisclosure.Erased then
                        Expect.stringContains
                            output
                            "Lifecycle: Erased"
                            "Erasure has its lifecycle label")
        ]
