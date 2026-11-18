module ClaimCore.Tests.PaymentCorrectionAcceptanceTests

open System
open System.IO
open System.Text
open Microsoft.VisualBasic.FileIO
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Wire.Export
open ClaimCore.Json
open ClaimCore.Cli
open ClaimCore.TestSupport
open ClaimCore.Tests.TaskBuilders

module Correct = ClaimCore.Domain.Tasks.Case.CorrectPayment

let private correct number selected date amount currency state =
    Correct.decide
        (context number)
        environment
        (Some(DecisionFacts.fromReplay state))
        (Binding.CasePayment(target state, state.Case.Revision, operation selected))
        (paymentInputs date amount currency)

let private finance (state: CaseFamily) =
    state.Case.Value.Content |> Option.get |> _.Finance

let private saturated caseRevision paymentRevision (state: CaseFamily) =
    let payment = state.Payments[operation 3]

    let part value id revision stamp =
        { initialChange with
            PartId = id
            NaturalKey = None
            Revision = revision
            Stamp = stamp
            Value = value
        }

    let root =
        part
            (PartValue.Case
                {
                    Reference = None
                    Lifecycle = Lifecycle.Active
                    Status = CaseStatus.Opened
                    Content = state.Case.Value.Content
                    Disposition = None
                })
            (CaseId.value state.Id)
            caseRevision
            state.Case.Stamp

    let selected =
        part
            (PartValue.Payment payment.Value)
            (OperationId.value (operation 3))
            paymentRevision
            payment.Stamp

    CaseFamily.restore
        state.Id
        state.Reference
        (Sequence.create Revision.Maximum |> Option.get)
        [ root; selected ]
        (Map.ofList
            [
                (PartKind.Case, root.PartId), initialChange.Stamp
                (PartKind.Payment, selected.PartId), payment.Stamp
            ])
    |> accepted

let private fixture () =
    let before = decided () |> pay 3 "2026-02-02" "25"
    let changes = correct 4 3 "2026-02-03" "40" "EUR" before |> accepted
    let after = apply before changes
    let snapshot = CaseFamily.snapshot after |> Option.get
    let differences = changes |> List.choose PartChange.paymentDifference
    let hash = EntryHash.ofBytes (Array.create 32 0xabuy) |> Option.get

    let receipt =
        { ResponseExamples.receipt with
            OperationId = operation 4
            Position = LedgerPosition.create (Sequence.create 4L |> Option.get) hash
            Subject =
                ReceiptSubject.Case
                    {
                        CaseId = after.Id
                        Reference = after.Reference
                        Revision = after.Case.Revision
                        Act = TaskId.ofLiteral "case.correctPayment"
                        Snapshot =
                            SnapshotDisclosure.Disclosed
                                {
                                    State = LiveCase.ofState snapshot.State |> Option.get
                                    Disposition = None
                                    RecordedFields = BusinessField.ofGroup RecordGroup.Payment
                                    Payments = differences
                                }
                    }
        }

    let descriptions =
        PartDescriptions.family (Some(DecisionFacts.fromReplay before)) Map.empty changes
        |> accepted

    let binding = Binding.CasePayment(target before, before.Case.Revision, operation 3)

    let submission =
        Submissions.create
            "case.correctPayment"
            (OperationId.value (operation 4))
            binding
            (Submissions.fields
                [
                    BusinessField.PaymentDate, "2026-02-03"
                    BusinessField.PaidAmount, "40"
                    BusinessField.PaidCurrency, "EUR"
                ])

    let review =
        {
            OperationReview.Task = TaskId.ofLiteral "case.correctPayment"
            Target = ReviewTarget.Case(before.Id, before.Reference)
            References = { Actors = []; Cases = [] }
            Binding = binding
            Changes = descriptions
            Context =
                {
                    ProductVersion = "fixture"
                    Basis = ResponseExamples.receipt.Basis
                }
        }

    before, after, changes, receipt, review, submission

let private cli reply =
    ReplyText.resultLinesWithDetails
        true
        (RenderingContext.forLanguage "en")
        None
        (FrameResult.Reply reply)
    |> String.concat "\n"

let private csvRows bytes =
    use source = new StringReader(Encoding.UTF8.GetString(bytes: byte array))
    use parser = new TextFieldParser(source)
    parser.SetDelimiters [| "," |]
    parser.HasFieldsEnclosedInQuotes <- true
    parser.TrimWhiteSpace <- false
    let headers = parser.ReadFields() |> nonNull |> Array.toList
    let rows = ResizeArray<_>()

    while not parser.EndOfData do
        let cells = parser.ReadFields() |> nonNull |> Array.toList
        Expect.equal cells.Length headers.Length "All encoded cells have their declared header"
        rows.Add(List.zip headers cells |> Map.ofList)

    List.ofSeq rows

[<Tests>]
let tests =
    testList
        "payment correction acceptance"
        [
            testCase
                "same amount is a new assertion while payable currency decision and registration stay unchanged"
                (fun () ->
                    let before = decided () |> pay 3 "2026-02-02" "250" |> pay 4 "2026-02-03" "100"

                    let after =
                        correct 5 3 "2026-02-04" "250" "EUR" before |> accepted |> apply before

                    Expect.equal
                        (finance after).PaidAmount
                        (finance before).PaidAmount
                        "Exact equal amount remains correctable"

                    Expect.equal
                        (finance after).Decision
                        (finance before).Decision
                        "Decision date payable amount and currency are immutable"

                    Expect.equal
                        after.Case.Value.Content.Value.Registration
                        before.Case.Value.Content.Value.Registration
                        "Correction asserts no registration changes"

                    Expect.isTrue
                        after.Payments[operation 3].Value.Removed
                        "Old assertion is removed even for equal money"

                    Expect.equal
                        (PaymentEntry.id after.Payments[operation 5].Value.Entry.Value)
                        (operation 5)
                        "Replacement identity equals correction operation")
            testCase "tied earliest and latest dates retain remaining extrema" (fun () ->
                let before =
                    decided ()
                    |> pay 3 "2026-02-02" "10"
                    |> pay 4 "2026-02-02" "20"
                    |> pay 5 "2026-02-05" "30"
                    |> pay 6 "2026-02-05" "40"

                let after = correct 7 3 "2026-02-04" "15" "EUR" before |> accepted |> apply before
                let after = correct 8 5 "2026-02-03" "25" "EUR" after |> accepted |> apply after
                let bounds = (finance after).PaymentDates |> Option.get

                Expect.equal
                    (CalendarDate.format bounds.Earliest, CalendarDate.format bounds.Latest)
                    ("2026-02-02", "2026-02-05")
                    "Other entries keep both tied extrema"

                Expect.equal
                    (Amount.value (finance after).PaidAmount)
                    100M
                    "Exact replacements reconcile the remaining set")
            testCase
                "replacement cannot change currency precede decision exceed today or assert nonpositive money"
                (fun () ->
                    let before = decided () |> pay 3 "2026-02-02" "25"

                    for date, amount, currency, violation in
                        [
                            "2026-02-03", "20", "USD", Violation.PaymentCurrencyMismatch
                            "2026-01-31", "20", "EUR", Violation.ChronologicalOrder
                            "2026-11-16", "20", "EUR", Violation.FutureDate
                            "2026-02-03", "0", "EUR", Violation.PaymentAmountPositive
                        ] do
                        let result = correct 4 3 date amount currency before
                        let issues = result |> expectInputs

                        Expect.isTrue
                            (issues |> List.exists (fun issue -> issue.Violation = violation))
                            "The independent intended rule refuses before changes exist"

                        Expect.equal
                            (Amount.value (finance before).PaidAmount)
                            25M
                            "Original immutable input remains intact")
            testCase "closed voided and erased cases require eligible current state" (fun () ->
                let paid = decided () |> pay 3 "2026-02-02" "25"

                for lifecycle, status, headline in
                    [
                        Lifecycle.Active, CaseStatus.Closed, CaseHeadline.Closed
                        Lifecycle.Voided, CaseStatus.Opened, CaseHeadline.Voided
                        Lifecycle.Erased, CaseStatus.Opened, CaseHeadline.Erased
                    ] do
                    let state = paid |> setCase lifecycle status

                    correct 5 3 "2026-02-03" "20" "EUR" state
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered headline)))
            testCase
                "saturated case and selected payment revisions refuse before increment"
                (fun () ->
                    let paid = decided () |> pay 3 "2026-02-02" "25"
                    let maximum = Revision.create Revision.Maximum |> Option.get
                    let saturatedCase = saturated maximum Revision.first paid

                    correct 4 3 "2026-02-03" "20" "EUR" saturatedCase
                    |> expectRefusal (Refusal.revisionConflict maximum)

                    let saturatedPayment = saturated paid.Case.Revision maximum paid

                    Expect.equal
                        (correct 4 3 "2026-02-03" "20" "EUR" saturatedPayment)
                        (Error(Rejection.InvalidState RecordViolation.InvalidRevisionSequence))
                        "Payment revision cannot wrap or create a plausible correction")
            testCase
                "DATA and HUMAN correction receipts preserve two ordered identities directions and final money"
                (fun () ->
                    let _, _, _, receipt, _, _ = fixture ()

                    let reply =
                        {
                            ExecuteReply.OperationId = OperationId.value receipt.OperationId
                            Outcome = ExecuteResult.Accepted receipt
                        }

                    let bytes = CliReplies.writeData Api.operationExecute reply |> WireWriter.bytes

                    let decoded =
                        Codec.decode Limits.MaximumResponseBytes Api.operationExecute.Output bytes
                        |> accepted

                    Expect.equal
                        decoded
                        (RepresentationReply.Data reply)
                        "Typed DATA producer and consumer carry the complete receipt"

                    let raw = Encoding.UTF8.GetString bytes

                    Expect.stringContains
                        raw
                        "\"payments\":["
                        "Only the plural current grammar is emitted"

                    Expect.isFalse
                        (raw.Contains "\"payment\":")
                        "Retired singular member is absent"

                    for expected in
                        [
                            OperationId.format (operation 3)
                            OperationId.format (operation 4)
                            "REMOVED"
                            "RECORDED"
                        ] do
                        Expect.stringContains
                            raw
                            expected
                            "Both directions and identities cross DATA"

                    let human = CliReplies.humanExecute reply
                    let text = cli human

                    for expected in
                        [
                            OperationId.format (operation 3)
                            OperationId.format (operation 4)
                            "Payment entry removed."
                            "Payment recorded."
                            "40.00 EUR"
                            "960.00 EUR"
                            String.replicate 32 "ab"
                        ] do
                        Expect.stringContains
                            text
                            expected
                            "CLI renders complete supplied HUMAN result and operation proof"

                    let document = HumanReceiptDocument.ofCore receipt

                    let table =
                        document.Screen.Regions
                        |> List.pick (fun region ->
                            if region.Role = RegionRole.Payments then
                                match region.Content with
                                | RegionContent.Table table -> Some table
                                | _ -> None
                            else
                                None)

                    Expect.equal table.Rows.Length 2 "No singleton receipt truncation"

                    let last row =
                        row.Values |> List.rev |> List.take 2 |> List.rev

                    Expect.equal
                        (table.Rows |> List.map last)
                        [
                            [
                                PresentedValue.Message "task.case.removePayment.done"
                                PresentedValue.Identifier(OperationId.format (operation 3))
                            ]
                            [
                                PresentedValue.Message "task.case.recordPayment.done"
                                PresentedValue.Identifier(OperationId.format (operation 4))
                            ]
                        ]
                        "Literal old-removed then new-recorded ordering")
            testCase
                "correction preview and CLI expose both selected and replacement identities without retargeting"
                (fun () ->
                    let _, _, _, receipt, review, submission = fixture ()

                    let preview =
                        {
                            PreviewReply.OperationId = OperationId.value receipt.OperationId
                            Outcome = Outcome.Succeeded(Previewed.Reviewed review)
                        }

                    let text = cli (CliReplies.humanPreview submission preview)

                    for expected in
                        [
                            "Correct payment entry"
                            OperationId.format (operation 3)
                            OperationId.format (operation 4)
                            "25.00 EUR"
                            "40.00 EUR"
                        ] do
                        Expect.stringContains
                            text
                            expected
                            "Preview retains old and replacement assertions"

                    let document = HumanReviewDocument.ofSubmission submission review

                    let amounts =
                        document.Presentation.Changes
                        |> List.filter (fun change ->
                            change.Key.EndsWith(".amount", StringComparison.Ordinal))

                    Expect.equal
                        (amounts |> List.map _.Key)
                        [
                            OperationId.format (operation 3) + ".amount"
                            OperationId.format (operation 4) + ".amount"
                        ]
                        "The selected removal precedes the replacement assertion"

                    match amounts with
                    | [ {
                            Before = PresentedValue.Money old
                            After = PresentedValue.Absent _
                        }
                        {
                            Before = PresentedValue.Absent _
                            After = PresentedValue.Money replacement
                        } ] ->
                        Expect.equal
                            (Money.value old.Value, Money.value replacement.Value)
                            (25M, 40M)
                            "Review removes the old amount and records the replacement in the correct direction"
                    | _ -> failtest "Two directed amount changes required"

                    let offered =
                        Screen.affordances document.Document.Screen @ document.Document.Actions

                    let control =
                        offered
                        |> List.find (fun action ->
                            action.Task = TaskId.ofLiteral "case.correctPayment")

                    match control.Control with
                    | ActionControl.Operation binding ->
                        Expect.equal
                            binding
                            review.Binding
                            "Review is bound to the exact selected old entry"
                    | _ -> failtest "Ledger operation binding required")
            testCase
                "timeline Wire and CSV retain each correction effect proof and literal payment direction"
                (fun () ->
                    let before, _, changes, receipt, review, _ = fixture ()

                    let entries =
                        List.map2
                            (fun change description ->
                                {
                                    CaseTimelineEntry.OperationId = receipt.OperationId
                                    Position = change.Stamp.Position
                                    OperationHash = receipt.Position.EntryHash
                                    Subject =
                                        {
                                            PartKind = change.Kind
                                            PartId = change.PartId
                                            Revision = change.Revision
                                        }
                                    Task = review.Task
                                    RecordedAt = receipt.RecordedAt
                                    RecordedBy =
                                        receipt.RecordedBy |> AuthorityRef.actor |> Option.get
                                    Changes =
                                        TimelineChanges.Disclosed(
                                            PartDescriptions.timeline Map.empty description
                                            |> accepted
                                        )
                                })
                            changes
                            review.Changes

                    let page =
                        {
                            CaseTimelinePage.Disclosure = HistoryDisclosure.Full
                            Basis = receipt.Basis
                            CaseId = before.Id
                            Reference = before.Reference
                            Items = entries
                            NextCursor = None
                        }

                    let encoded =
                        CliReplies.writeData Api.caseTimeline (Outcome.Succeeded page)
                        |> WireWriter.bytes

                    Expect.isOk
                        (Codec.decode Limits.MaximumResponseBytes Api.caseTimeline.Output encoded)
                        "Actual timeline Wire accepts all3 correction effects"

                    let raw = Encoding.UTF8.GetString encoded

                    for expected in
                        [
                            OperationId.format (operation 3)
                            OperationId.format (operation 4)
                            "REMOVED"
                            "RECORDED"
                        ] do
                        Expect.stringContains
                            raw
                            expected
                            "Timeline exposes the applicable payment changes"

                    let rows =
                        Array.append TimelineCsv.csvHeader (TimelineCsv.csvRows page) |> csvRows

                    let field id =
                        "payment." + OperationId.format (operation id) + ".paidAmount"

                    let removed = rows |> List.find (fun row -> row["field"] = field 3)
                    let recorded = rows |> List.find (fun row -> row["field"] = field 4)

                    Expect.equal
                        (removed["before"], removed["after"], removed["effectIndex"])
                        ("25", "", "0")
                        "Old amount belongs to removal effect0"

                    Expect.equal
                        (recorded["before"], recorded["after"], recorded["effectIndex"])
                        ("", "40", "1")
                        "Replacement amount belongs to recording effect1"

                    for row in [ removed; recorded ] do
                        Expect.equal
                            row["operationId"]
                            (OperationId.format (operation 4))
                            "One correction operation"

                        Expect.equal row["sequence"] "4" "Shared accepting operation sequence"

                        Expect.equal
                            row["operationHash"]
                            (String.replicate 32 "ab")
                            "Existing operation proof is preserved")
            testCase
                "withheld and erased correction receipts retain proof without leaking either payment"
                (fun () ->
                    let _, _, _, receipt, _, _ = fixture ()

                    for disclosure in [ SnapshotDisclosure.Withheld; SnapshotDisclosure.Erased ] do
                        let receipt =
                            match receipt.Subject with
                            | ReceiptSubject.Case value ->
                                { receipt with
                                    Subject =
                                        ReceiptSubject.Case { value with Snapshot = disclosure }
                                }
                            | _ -> failtest "Case receipt required"

                        let text =
                            cli (
                                CliReplies.humanExecute
                                    {
                                        ExecuteReply.OperationId =
                                            OperationId.value receipt.OperationId
                                        Outcome = ExecuteResult.Accepted receipt
                                    }
                            )

                        Expect.isFalse
                            (text.Contains(OperationId.format (operation 3)))
                            "Old private payment identity is not disclosed"

                        Expect.isFalse
                            (text.Contains "25.00 EUR" || text.Contains "40.00 EUR")
                            "Neither payment amount leaks"

                        Expect.stringContains
                            text
                            (String.replicate 32 "ab")
                            "Permitted acceptance proof remains")
        ]
