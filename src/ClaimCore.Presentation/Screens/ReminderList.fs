namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module ReminderListScreen =
    let private dueDate =
        column
            "due"
            "field.reminderDueDate.label"
            Prominence.Primary
            false
            (fun (item: ReminderReadModel) -> PresentedValue.Date(Reminder.due item.Reminder))

    let private queueDue =
        { dueDate with
            Read =
                fun item ->
                    let due = Reminder.due item.Reminder

                    match item.Active, Reminder.state item.Reminder, item.Timing with
                    | true, ReminderState.Pending, Some timing ->
                        PresentedValue.DateWithCode(
                            due,
                            (Vocabulary.name ReminderDue.vocabulary, ReminderDue.token timing)
                        )
                    | _ -> PresentedValue.Date due
        }

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
            dueDate
            column "state" "input.reminderState.label" Prominence.Secondary false (fun item ->
                code ReminderState.vocabulary (Reminder.state item.Reminder))
            column "kind" "field.kind.label" Prominence.Secondary false (fun item ->
                code ReminderKind.vocabulary (Reminder.kind item.Reminder))
            column "doneOn" "field.doneOn.label" Prominence.Secondary false (fun item ->
                optional PresentedValue.Date (Reminder.doneOn item.Reminder))
            column "deadlineOutcome" "label.deadlineOutcome" Prominence.Secondary false (fun item ->
                optional (code DeadlineOutcome.vocabulary) item.DeadlineOutcome)
            column "timing" "input.reminderDue.label" Prominence.Primary false (fun item ->
                if not item.Active then
                    code CaseDue.vocabulary CaseDue.Inactive
                else
                    optional (code ReminderDue.vocabulary) item.Timing)
            column "claimants" "label.claimants" Prominence.Secondary false (fun item ->
                match item.Claimants with
                | [] -> optional PresentedValue.Text None
                | names ->
                    PresentedValue.Text(names |> List.map PartyName.format |> String.concat "; "))
            column
                "responsible"
                "field.responsibleActor.label"
                Prominence.Secondary
                false
                (fun item ->
                    item.Responsible
                    |> Option.map PresentedValue.Actor
                    |> Option.defaultValue (PresentedValue.Absent "view.UNASSIGNED.label"))
            column "id" "field.reminderId.label" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(ReminderId.format (Reminder.id item.Reminder)))
        ]



    let columnsFor view state =
        let columns = [ "due"; "action"; "reference"; "claimants"; "kind"; "responsible" ]

        let completed =
            match view, state with
            | Some(ReminderView.Completed | ReminderView.Cancelled), _
            | None,
              Some(ReminderStateSelection.Any | ReminderStateSelection.State(ReminderState.Completed | ReminderState.Cancelled)) ->
                true
            | _ -> false

        let keys =
            if completed then
                columns @ [ "doneOn"; "deadlineOutcome" ]
            else
                columns

        keys
        |> List.map (function
            | "due" -> queueDue
            | key -> reminderColumns |> List.find (fun column -> column.Key = key))

    let recommendCompletion (table: RegionTable) =
        let complete = ClientCompositions.reminderCompletion

        { table with
            Rows =
                table.Rows
                |> List.map (fun row ->
                    { row with
                        Recommended =
                            row.Actions
                            |> List.tryFind (fun action ->
                                action.Task = complete && action.Blocked.IsNone)
                            |> Option.map _.Task
                    })
        }

    let rows selection state rows =
        let columns = columnsFor selection state

        let data =
            tableWithControls
                (fun (row: ReminderReadModel) -> ReminderId.format (Reminder.id row.Reminder))
                (fun _ -> None)
                columns
                (fun row ->
                    Some(
                        Link.ReminderGet
                            {
                                Target = CaseSelector.Id(Reminder.caseId row.Reminder)
                                ReminderId = ReminderId.value (Reminder.id row.Reminder)
                            }
                    ),
                    TaskAffordances.reminder row)
                rows
            |> recommendCompletion

        let groups =
            [
                "reminderDue.OVERDUE.label",
                (fun (row: ReminderReadModel) -> row.Timing = Some ReminderDue.Overdue)
                "reminderDue.TODAY.label", (fun row -> row.Timing = Some ReminderDue.Today)
                "reminderDue.UPCOMING.label", (fun row -> row.Timing = Some ReminderDue.Upcoming)
                "reminderState.COMPLETED.label",
                (fun row -> row.Active && Reminder.state row.Reminder = ReminderState.Completed)
                "reminderState.CANCELLED.label",
                (fun row -> row.Active && Reminder.state row.Reminder = ReminderState.Cancelled)
                "label.reminderCompletedInactive",
                (fun row -> not row.Active && Reminder.state row.Reminder = ReminderState.Completed)
                "label.reminderCancelledInactive",
                (fun row -> not row.Active && Reminder.state row.Reminder = ReminderState.Cancelled)
                (Vocabulary.messageKey CaseDue.vocabulary CaseDue.Inactive "label"),
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

        let dueGroups =
            groups
            |> List.collect (fun group -> group.RowKeys |> List.map (fun key -> key, group.TitleId))
            |> Map.ofList

        let data =
            { data with
                Groups = groups
                Rows =
                    data.Rows
                    |> List.map (fun row ->
                        { row with
                            Values =
                                row.Values
                                |> List.map (function
                                    | PresentedValue.DateWithCode(date, (vocabulary, token)) when
                                        Map.tryFind row.Key dueGroups =
                                            Some(vocabulary + "." + token + ".label")
                                        ->
                                        PresentedValue.Date date
                                    | value -> value)
                        })
            }

        Screen.create
            [
                region RegionRole.Results None (RegionContent.Table data)
                |> withEmpty "label.noReminders"
            ]

    let view selection state (reply: ReminderPage) = rows selection state reply.Items

    let document (request: ReminderListRequest) (value: ReminderPage) =
        HumanDocument.listPage value.Basis (view request.View request.State value) value.NextCursor
