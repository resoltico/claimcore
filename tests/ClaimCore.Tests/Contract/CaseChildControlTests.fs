module ClaimCore.Tests.CaseChildControlTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private accepted result =
    result |> Result.defaultWith (string >> failtest)

let private read () =
    let payment = Inputs.admittedPayment Inputs.payment

    let finance =
        FinancialPosition.undecided
        |> FinancialPosition.withDecision (Some(Inputs.admittedDecision Inputs.decision))
        |> accepted

    let finance =
        FinancialPosition.record Inputs.workBasis.BusinessDate payment finance
        |> accepted

    let snapshot =
        { ResponseExamples.caseRead.Snapshot with
            State =
                ResponseExamples.caseRead.Snapshot.State
                |> CaseState.map (fun fields -> { fields with Finance = finance })
        }

    let advice =
        CaseAdvice.create
            [ CaseActKind.RemovePayment ]
            None
            HistoryAccess.None
            ((Inputs.caseAdvice snapshot.State).Blocked
             |> List.filter (fun item -> item.Act <> CaseActKind.RemovePayment))
        |> accepted

    let reminderId =
        ReminderId.create (Guid.Parse "00000000-0000-4000-8000-000000000002")
        |> Option.get

    let reminder =
        Reminder.ofAccepted
            reminderId
            snapshot.CaseId
            Revision.first
            (ReminderAction.parse "Core action seed" |> accepted)
            (CalendarDate.ofDate Inputs.workBasis.BusinessDate)
            ReminderState.Pending
        |> accepted

    let next =
        {
            ReminderReadModel.Reminder = reminder
            Reference = snapshot.Reference
            ClaimantName = Inputs.name "Core claimant"
            Responsible = None
            RecordedAt = Inputs.workBasis.ObservedAt
            RecordedBy = ResponseExamples.caseRead.RecordedBy
            Acts = [ ReminderActKind.Complete ]
            Active = true
            Timing = Some ReminderDue.Today
        }

    { ResponseExamples.caseRead with
        Snapshot = snapshot
        Advice = advice
        LatestPayments = [ PaymentEntry.recorded ResponseExamples.receipt.OperationId payment ]
        NextActions = [ next ]
    }

let private rowActions document role =
    document.Screen.Regions
    |> List.filter (fun region -> region.Role = role)
    |> List.collect (fun region ->
        match region.Content with
        | RegionContent.Table table -> table.Rows |> List.collect _.Actions
        | _ -> [])

[<Tests>]
let tests =
    testList
        "case child controls"
        [
            testCase "payment and reminder rows carry their own ready controls" (fun () ->
                let value = read ()
                let document = HumanCaseDocument.ofCore value
                let payment = rowActions document RegionRole.Payments |> List.exactlyOne

                Expect.equal
                    payment.Task
                    (TaskId.Operation(ActKind.Case CaseActKind.RemovePayment))
                    "Only the offered row task"

                Expect.equal
                    payment.Binding
                    (OperationBinding.Case(
                        CaseSelector.Id value.Snapshot.CaseId,
                        Revision.value value.Snapshot.Revision,
                        Some(OperationId.value ResponseExamples.receipt.OperationId)
                    ))
                    "Payment identity and version stay together"

                let reminder = rowActions document RegionRole.NextActions |> List.exactlyOne

                Expect.equal
                    reminder.Task
                    (TaskId.Operation(ActKind.Reminder ReminderActKind.Complete))
                    "The selected reminder offers completion"

                Expect.isEmpty reminder.Seeds "Completion needs no editable action or date"

                Expect.isOk
                    (Codec.encode HumanDocumentCodecs.document document
                     |> Codec.decode 65536 HumanDocumentCodecs.document)
                    "The actual wire model round trips")
            testCase
                "payment currency prefill belongs only to the offered recording task"
                (fun () ->
                    let value = read ()

                    let advice =
                        CaseAdvice.create
                            [ CaseActKind.RecordPayment ]
                            None
                            HistoryAccess.None
                            ((Inputs.caseAdvice value.Snapshot.State).Blocked
                             |> List.filter (fun item -> item.Act <> CaseActKind.RecordPayment))
                        |> accepted

                    let offered = HumanCaseDocument.ofCore { value with Advice = advice }

                    let record =
                        offered.Actions
                        |> List.find (fun action ->
                            action.Task = TaskId.Operation(ActKind.Case CaseActKind.RecordPayment)
                            && action.Blocked.IsNone)

                    Expect.equal
                        (Map.find (TaskAffordances.field BusinessField.PaidCurrency) record.Seeds)
                        (SubmissionValue.Text "EUR")
                        "Decision currency is the authored default"

                    let unavailable = HumanCaseDocument.ofCore value

                    Expect.isFalse
                        (unavailable.Actions
                         |> List.exists (fun action ->
                             action.Blocked.IsNone
                             && Map.containsKey
                                 (TaskAffordances.field BusinessField.PaidCurrency)
                                 action.Seeds))
                        "Other tasks do not acquire that input seed")
        ]
