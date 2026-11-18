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
                "kind"
                "doneOn"
                "deadlineOutcome"
                "recordedAt"
                "recordedBy.actorId"
                "recordedBy.displayName"
                "active"
            ]

    let csvChunks render rows =
        rows
        |> Seq.map (fun (row: ReminderReadModel) ->
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
                    ReminderKind.token (Reminder.kind value)
                    Reminder.doneOn value
                    |> Option.map CalendarDate.format
                    |> Option.defaultValue ""
                    row.DeadlineOutcome
                    |> Option.map DeadlineOutcome.token
                    |> Option.defaultValue ""
                    Csv.instant row.RecordedAt
                    ActorId.format row.RecordedBy.ActorId
                    Csv.actorName render row.RecordedBy
                    Csv.boolean row.Active
                ])

    let csvRows render rows =
        csvChunks render rows |> Seq.toArray |> Array.concat
