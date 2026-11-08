namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module ReminderScreen =
    let reminder (reply: ReminderReadModel) =
        let current = reply.Reminder

        [
            {
                TitleId = "label.reminder"
                Entries =
                    [
                        yield
                            value
                                "caseReference"
                                "field.caseReference.label"
                                Prominence.Primary
                                (PresentedValue.Reference reply.Reference)
                        yield
                            value
                                "reminderId"
                                "field.reminderId.label"
                                Prominence.Detail
                                (PresentedValue.Identifier(ReminderId.format (Reminder.id current)))
                        yield
                            value
                                "revision"
                                "label.reminderRevision"
                                Prominence.Detail
                                (PresentedValue.Identifier(
                                    Revision.format (Reminder.revision current)
                                ))
                        yield
                            value
                                "action"
                                "field.reminderAction.label"
                                Prominence.Primary
                                (PresentedValue.Text(
                                    ReminderAction.format (Reminder.action current)
                                ))
                        yield
                            value
                                "dueDate"
                                "field.reminderDueDate.label"
                                Prominence.Primary
                                (PresentedValue.Date(Reminder.due current))
                        yield
                            value
                                "state"
                                "input.reminderState.label"
                                Prominence.Primary
                                (PresentedValue.Code(
                                    Vocabulary.name ReminderState.vocabulary,
                                    ReminderState.token (Reminder.state current)
                                ))
                        match reply.Timing with
                        | Some timing ->
                            yield
                                value
                                    "timing"
                                    "input.reminderDue.label"
                                    Prominence.Secondary
                                    (PresentedValue.Code(
                                        Vocabulary.name ReminderDue.vocabulary,
                                        Vocabulary.token ReminderDue.vocabulary timing
                                    ))
                        | None -> ()
                        if not reply.Active then
                            yield
                                value
                                    "parentState"
                                    "field.status.label"
                                    Prominence.Secondary
                                    (PresentedValue.Code(
                                        Vocabulary.name CaseDue.vocabulary,
                                        Vocabulary.token CaseDue.vocabulary CaseDue.Inactive
                                    ))
                        yield
                            value
                                "recordedAt"
                                "label.recordedAt"
                                Prominence.Detail
                                (PresentedValue.Instant reply.RecordedAt)
                        yield
                            value
                                "recordedBy"
                                "label.recordedBy"
                                Prominence.Secondary
                                (PresentedValue.Actor reply.RecordedBy)
                    ]
            }
        ]

    let view (reply: ReminderReadModel) =
        let all = reminder reply |> List.collect _.Entries
        let identifiers, facts = all |> List.partition technical

        let identity, body =
            facts |> List.partition (fun entry -> entry.Key = "caseReference")

        Screen.create
            [
                entryRegion RegionRole.Identity None identity
                entryRegion RegionRole.Facts (Some "label.reminder") body
                entryRegion RegionRole.Details (Some "label.recordDetails") identifiers
            ]
