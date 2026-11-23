module ClaimCore.Tests.ReminderControlDocumentTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Cli
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
        Reminder.decode
            id
            parent
            Revision.first
            (ReminderAction.parse "Editable action seed"
             |> Result.defaultWith (string >> failtest))
            (CalendarDate.ofDate DomainInputs.workBasis.BusinessDate)
            ReminderKind.FollowUp
            ReminderState.Pending
            None
        |> Result.defaultWith failtest

    {
        ReminderReadModel.Reminder = reminder
        Reference = ResponseExamples.caseRead.Snapshot.Reference
        Claimants = [ (DomainInputs.name "Read-only claimant") ]
        Responsible = None
        RecordedAt = DomainInputs.workBasis.ObservedAt
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
                (HumanReadDocuments.reminders (
                    {
                        View = None
                        Target = None
                        State = None
                        Kind = None
                        DeadlineOutcome = None
                        Due = None
                        Assignment = None
                        Text = None
                        AfterCursor = None
                        Limit = 50
                        IncludeInactive = None
                    }
                ))
                    {
                        Basis = DomainInputs.workBasis
                        Items = [ item ]
                        NextCursor = None
                    }

            match document.Screen.Regions.Head.Content with
            | RegionContent.Table table ->
                Expect.equal
                    table.Rows.Head.Recommended
                    (Some(TaskId.ofLiteral "reminder.complete"))
                    "A pending row recommends its offered completion"

                let action =
                    table.Rows.Head.Actions
                    |> List.find (fun action -> Some action.Task = table.Rows.Head.Recommended)

                Expect.equal
                    action.ShortLabelId
                    (Some "task.reminder.complete.shortLabel")
                    "The recommendation supplies a compact label"

                for language in LocalizationPolicy.languages do
                    let catalog = MessageCatalogs.render language
                    let shortLabel = catalog "task.reminder.complete.shortLabel" Map.empty
                    Expect.isNonEmpty shortLabel "Every offered language renders the short label"
            | _ -> failtest "Reminder results are a table"

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
                        Basis = DomainInputs.workBasis
                        Item = item
                    }

            Expect.equal detail.Actions controls "The two presenters reuse the owner projection"

            Expect.isOk
                (Codec.encode HumanDocumentCodecs.document document
                 |> Codec.decode 65536 HumanDocumentCodecs.document)
                "Actual wire boundary")
