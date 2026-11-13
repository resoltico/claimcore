namespace ClaimCore.Wire.Export

open ClaimCore.Domain
open ClaimCore.Application.Contracts

module ReminderCsv =
    let csvHeader =
        Csv.row
            [
                "caseId"
                "caseReference"
                "reminderId"
                "revision"
                "action"
                "dueDate"
                "state"
                "recordedAt"
                "recordedBy.actorId"
                "recordedBy.displayName"
                "active"
            ]

    let csvRows render rows =
        rows
        |> List.map (fun (row: ReminderReadModel) ->
            let value = row.Reminder

            Csv.row
                [
                    CaseId.format (Reminder.caseId value)
                    CaseReference.format row.Reference
                    ReminderId.format (Reminder.id value)
                    Revision.format (Reminder.revision value)
                    ReminderAction.format (Reminder.action value)
                    CalendarDate.format (Reminder.due value)
                    ReminderState.token (Reminder.state value)
                    Csv.instant row.RecordedAt
                    ActorId.format row.RecordedBy.ActorId
                    Csv.actorName render row.RecordedBy
                    Csv.boolean row.Active
                ])
        |> Array.concat
