namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts

/// Aggregate every view in one statement over the authorized snapshot, before any paging.
module internal ViewCountStorage =
    let private countColumns views predicate =
        views
        |> List.indexed
        |> List.map (fun (index, view) ->
            $"count(*) FILTER(WHERE ({predicate view})) AS view{index}")
        |> String.concat ","

    let cases connection (selection: CaseSelection) token =
        task {
            let columns = countColumns CaseView.all CaseSelectionSql.viewPredicate

            let! rows =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {columns} FROM claimcore.cases c WHERE {CaseSelectionSql.predicate selection}")
                    (CaseSelectionSql.parameters selection)
                    token

            let row = List.exactlyOne rows

            return
                CaseView.all
                |> List.mapi (fun index view ->
                    {
                        CaseViewCount.View = view
                        Count = DatabaseSession.namedValue<int64> ($"view{index}") row
                    })
        }

    let reminderPredicate =
        let actionable = "r.state='PENDING' AND c.state='ACTIVE' AND c.status='OPENED'"

        function
        | ReminderView.Pending -> actionable
        | ReminderView.Mine -> $"{actionable} AND c.responsible_actor=$2"
        | ReminderView.Overdue -> $"{actionable} AND r.due<$11::date"
        | ReminderView.Today -> $"{actionable} AND r.due=$11::date"
        | ReminderView.Upcoming -> $"{actionable} AND r.due>$11::date"
        | ReminderView.Completed -> "c.state<>'ERASED' AND r.state='COMPLETED'"
        | ReminderView.Cancelled -> "c.state<>'ERASED' AND r.state='CANCELLED'"

    let reminders connection (selection: CaseSelection) token =
        task {
            let columns = countColumns ReminderView.all reminderPredicate

            let! rows =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {columns} FROM claimcore.reminders r JOIN claimcore.cases c USING(case_id) WHERE c.state<>'ERASED' AND {CaseSelectionSql.predicate selection}")
                    (CaseSelectionSql.parameters selection)
                    token

            let row = List.exactlyOne rows

            return
                ReminderView.all
                |> List.mapi (fun index view ->
                    {
                        ReminderViewCount.View = view
                        Count = DatabaseSession.namedValue<int64> ($"view{index}") row
                    })
        }
