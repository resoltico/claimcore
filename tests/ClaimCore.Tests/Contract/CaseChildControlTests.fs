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

[<Tests>]
let tests =
    testCase "current case binds bounded payment and reminder controls to supplied rows" (fun () ->
        let value = read ()
        let document = HumanCaseDocument.ofCore value

        match document.Context with
        | InteractionContext.Case control ->
            Expect.equal
                (List.exactlyOne control.Payments).PaymentId
                ResponseExamples.receipt.OperationId
                "Retained payment identity"

            Expect.isTrue (List.exactlyOne control.Payments).CanRemove "Authorized removal control"
            let next = List.exactlyOne control.NextActions
            Expect.equal next.CaseId value.Snapshot.CaseId "Parent identity"
            Expect.equal next.Revision Revision.first "Selected reminder revision"

            Expect.equal
                next.Tasks
                [ TaskId.Operation(ActKind.Reminder ReminderActKind.Complete) ]
                "Offered reminder task only"

            Expect.isNone next.ActionSeed "Completion discloses no editable action"
        | _ -> failtest "Case controls required"

        let bytes = Codec.encode HumanDocumentCodecs.document document

        Expect.isOk
            (Codec.decode 65536 HumanDocumentCodecs.document bytes)
            "Actual native boundary"

        for mutate in
            [
                (fun (context: JsonNode) -> context["payments"] <- JsonArray())
                (fun context -> context["nextActions"] <- JsonArray())
                (fun context -> context["tasks"] <- JsonArray())
            ] do
            let node = JsonNode.Parse(Encoding.UTF8.GetString bytes) |> nonNull
            mutate (nonNull node["context"])

            Expect.isError
                (Encoding.UTF8.GetBytes(node.ToJsonString())
                 |> Codec.decode 65536 HumanDocumentCodecs.document)
                "Missing or unoffered controls refused")
