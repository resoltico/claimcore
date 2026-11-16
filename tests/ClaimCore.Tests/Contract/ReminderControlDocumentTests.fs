module ClaimCore.Tests.ReminderControlDocumentTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private item =
    let id =
        ReminderId.create (Guid.Parse "00000000-0000-4000-8000-000000000002")
        |> Option.get

    let parent = ResponseExamples.caseRead.Snapshot.CaseId

    let reminder =
        Reminder.ofAccepted
            id
            parent
            Revision.first
            (ReminderAction.parse "Editable action seed"
             |> Result.defaultWith (string >> failtest))
            (CalendarDate.ofDate Inputs.workBasis.BusinessDate)
            ReminderState.Pending
        |> Result.defaultWith failtest

    {
        ReminderReadModel.Reminder = reminder
        Reference = ResponseExamples.caseRead.Snapshot.Reference
        ClaimantName = Inputs.name "Read-only claimant"
        Responsible = None
        RecordedAt = Inputs.workBasis.ObservedAt
        RecordedBy = ResponseExamples.caseRead.RecordedBy
        Offers =
            [
                Inputs.offeredTask
                    "reminder.update"
                    (Binding.Reminder(CaseSelector.Id parent, id, Revision.first))
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.ReminderAction,
                            InputValue.Text "Editable action seed"
                            TaskInputId.Target InputTarget.ReminderDueDate,
                            InputValue.Text(CalendarDate.format (Reminder.due reminder))
                        ])
                Inputs.offeredTask
                    "reminder.complete"
                    (Binding.Reminder(CaseSelector.Id parent, id, Revision.first))
                    Map.empty
            ]
        Active = true
        Timing = Some ReminderDue.Today
    }

[<Tests>]
let tests =
    testCase
        "list and detail carry the same versioned reminder affordances and permitted seeds"
        (fun () ->
            let document =
                HumanReadDocuments.reminders
                    {
                        Basis = Inputs.workBasis
                        Items = [ item ]
                        NextCursor = None
                    }

            let controls = Screen.affordances document.Screen

            Expect.equal
                (controls |> List.map _.Task)
                [ TaskId.ofLiteral "reminder.update"; TaskId.ofLiteral "reminder.complete" ]
                "Literal offered task set"

            for control in controls do
                Expect.equal
                    control.Binding
                    (Some(
                        Binding.Reminder(
                            CaseSelector.Id(Reminder.caseId item.Reminder),
                            Reminder.id item.Reminder,
                            Revision.first
                        )
                    ))
                    "The exact selected version binds both tasks"

            let update = controls.Head

            Expect.equal
                (Map.find (TaskAffordances.input InputTarget.ReminderAction) update.Seeds)
                (InputValue.Text "Editable action seed")
                "Only update offers the action seed"

            Expect.isEmpty controls.Tail.Head.Seeds "Completion carries no editable action or date"

            let detail =
                HumanReadDocuments.reminder
                    {
                        Basis = Inputs.workBasis
                        Item = item
                    }

            Expect.equal detail.Actions controls "The two presenters reuse the owner projection"

            Expect.isOk
                (Codec.encode HumanDocumentCodecs.document document
                 |> Codec.decode 65536 HumanDocumentCodecs.document)
                "Actual wire boundary")
