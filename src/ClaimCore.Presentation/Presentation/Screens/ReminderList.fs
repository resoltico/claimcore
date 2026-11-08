namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module ReminderListScreen =
    let reminderColumns =
        [
            column
                "reference"
                "field.caseReference.label"
                Prominence.Primary
                false
                (fun (item: ReminderReadModel) -> PresentedValue.Reference item.Reference)
            column "action" "field.reminderAction.label" Prominence.Primary false (fun item ->
                PresentedValue.Text(ReminderAction.format (Reminder.action item.Reminder)))
            column "due" "field.reminderDueDate.label" Prominence.Primary false (fun item ->
                PresentedValue.Date(Reminder.due item.Reminder))
            column "state" "input.reminderState.label" Prominence.Secondary false (fun item ->
                code ReminderState.vocabulary (Reminder.state item.Reminder))
            column "timing" "input.reminderDue.label" Prominence.Primary false (fun item ->
                if not item.Active then
                    code CaseDue.vocabulary CaseDue.Inactive
                else
                    optional (code ReminderDue.vocabulary) item.Timing)
            column "recordedBy" "label.recordedBy" Prominence.Detail false (fun item ->
                PresentedValue.Actor item.RecordedBy)
            column "recordedAt" "label.recordedAt" Prominence.Detail false (fun item ->
                PresentedValue.Instant item.RecordedAt)
            column "id" "field.reminderId.label" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(ReminderId.format (Reminder.id item.Reminder)))
        ]



    let rows rows =
        let columns = reminderColumns |> List.filter (fun column -> column.Key <> "id")

        let data =
            table
                (fun (row: ReminderReadModel) -> ReminderId.format (Reminder.id row.Reminder))
                (fun _ -> None)
                columns
                rows

        let groups =
            [
                "reminderDue.OVERDUE.label",
                (fun (row: ReminderReadModel) -> row.Timing = Some ReminderDue.Overdue)
                "reminderDue.TODAY.label", (fun row -> row.Timing = Some ReminderDue.Today)
                "reminderDue.UPCOMING.label", (fun row -> row.Timing = Some ReminderDue.Upcoming)
                "reminderState.COMPLETED.label",
                (fun row -> Reminder.state row.Reminder = ReminderState.Completed)
                "reminderState.CANCELLED.label",
                (fun row -> Reminder.state row.Reminder = ReminderState.Cancelled)
                "caseDue.INACTIVE.label",
                (fun row ->
                    row.Timing.IsNone && Reminder.state row.Reminder = ReminderState.Pending)
            ]
            |> List.choose (fun (title, accepts) ->
                let keys =
                    rows
                    |> List.filter accepts
                    |> List.map (fun row -> ReminderId.format (Reminder.id row.Reminder))

                if keys.IsEmpty then
                    None
                else
                    Some
                        {
                            RegionRowGroup.TitleId = title
                            Count = int64 keys.Length
                            RowKeys = keys
                        })

        Screen.create
            [
                region RegionRole.Results None (RegionContent.Table { data with Groups = groups })
            ]

    let view (reply: ReminderPage) =
        page reply.Basis reply.Items.Length reply.NextCursor (rows reply.Items)
