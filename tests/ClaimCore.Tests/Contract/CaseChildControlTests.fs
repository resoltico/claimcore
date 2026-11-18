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
    let payment = DomainInputs.admittedPayment DomainInputs.payment

    let finance =
        FinancialPosition.undecided
        |> FinancialPosition.withDecision (
            Some(DomainInputs.admittedDecision DomainInputs.decision)
        )

    let finance =
        FinancialPosition.add
            (PaymentEntry.recorded
                (CaseFixtures.operationId (System.Guid.Parse "10000000-0000-7000-8000-000000000001"))
                payment)
            finance
        |> accepted

    let snapshot =
        { ResponseExamples.caseRead.Snapshot with
            State =
                ResponseExamples.caseRead.Snapshot.State
                |> CaseState.map (fun fields -> { fields with Finance = finance })
        }

    let advice =
        CaseAdvice.create
            [ (TaskId.ofLiteral "case.removePayment") ]
            None
            HistoryAccess.None
            ((Inputs.caseAdvice snapshot.State).Blocked
             |> List.filter (fun item -> item.Act <> (TaskId.ofLiteral "case.removePayment")))
        |> accepted

    let reminderId =
        ReminderId.create (Guid.Parse "00000000-0000-4000-8000-000000000002")
        |> Option.get

    let reminder =
        Reminder.decode
            reminderId
            snapshot.CaseId
            Revision.first
            (ReminderAction.parse "Core action seed" |> accepted)
            (CalendarDate.ofDate DomainInputs.workBasis.BusinessDate)
            ReminderKind.FollowUp
            ReminderState.Pending
            None
        |> accepted

    let next =
        {
            ReminderReadModel.Reminder = reminder
            Reference = snapshot.Reference
            Claimants = [ (DomainInputs.name "Core claimant") ]
            Responsible = None
            RecordedAt = DomainInputs.workBasis.ObservedAt
            RecordedBy = ResponseExamples.caseRead.RecordedBy
            Offers =
                [
                    Inputs.offeredTask
                        "reminder.complete"
                        (Binding.Reminder(
                            CaseSelector.Id snapshot.CaseId,
                            reminderId,
                            Revision.first
                        ))
                        Map.empty
                ]
            Active = true
            Timing = Some ReminderDue.Today
        }

    { ResponseExamples.caseRead with
        Snapshot = snapshot
        Advice = advice
        LatestPayments = [ PaymentEntry.recorded ResponseExamples.receipt.OperationId payment ]
        PaymentPayees = []
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
                    (TaskId.ofLiteral "case.removePayment")
                    "Only the offered row task"

                Expect.equal
                    payment.Binding
                    (Some(
                        Binding.CasePayment(
                            CaseSelector.Id value.Snapshot.CaseId,
                            value.Snapshot.Revision,
                            ResponseExamples.receipt.OperationId
                        )
                    ))
                    "Payment identity and version stay together"

                let reminder = rowActions document RegionRole.NextActions |> List.exactlyOne

                Expect.equal
                    reminder.Task
                    (TaskId.ofLiteral "reminder.complete")
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
                            [ (TaskId.ofLiteral "case.recordPayment") ]
                            None
                            HistoryAccess.None
                            ((Inputs.caseAdvice value.Snapshot.State).Blocked
                             |> List.filter (fun item ->
                                 item.Act <> (TaskId.ofLiteral "case.recordPayment")))
                        |> accepted
                        |> CaseAdvice.withOffers
                            [
                                Inputs.offeredTask
                                    "case.recordPayment"
                                    (Binding.Case(
                                        CaseSelector.Id value.Snapshot.CaseId,
                                        value.Snapshot.Revision
                                    ))
                                    (Map.ofList
                                        [
                                            TaskInputId.Target(
                                                InputTarget.Field BusinessField.PaidCurrency
                                            ),
                                            InputValue.Text "EUR"
                                        ])
                            ]

                    let offered = HumanCaseDocument.ofCore { value with Advice = advice }

                    let record =
                        offered.Actions
                        |> List.find (fun action ->
                            action.Task = (TaskId.ofLiteral "case.recordPayment")
                            && action.Blocked.IsNone)

                    Expect.equal
                        (Map.find (TaskAffordances.field BusinessField.PaidCurrency) record.Seeds)
                        (InputValue.Text "EUR")
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
