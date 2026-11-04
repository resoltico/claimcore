namespace ClaimCore.Postgres

open System
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// All current work disclosure uses bounded pages and fixed-count page-oriented queries.
module internal WorkStorage =
    let private parsed =
        function
        | Ok value -> value
        | Error _ -> raise (RecordCorruptException "Invalid work projection bytes.")

    let private caseId name row =
        DatabaseSession.namedValue<Guid> name row
        |> DatabaseSession.admitted CaseId.create

    let private revision name row =
        DatabaseSession.namedValue<int64> name row
        |> DatabaseSession.admitted Revision.create

    let private actor name row =
        DatabaseSession.namedValue<Guid> name row
        |> DatabaseSession.admitted ActorId.create

    let private actorOption name row =
        DatabaseSession.namedOptional<Guid> name row
        |> Option.map (DatabaseSession.admitted ActorId.create)

    let workColumns =
        "case_id,revision,snapshot,responsible_actor,handling_summary,search_text,"
        + DatabaseSession.instantExpression "changed_at"
        + " AS changed_at,recorded_by"

    let reminderColumns =
        "r.reminder_id,r.case_id,r.revision,r.snapshot,r.state,r.action,"
        + DatabaseSession.dateExpression "r.due"
        + " AS due,r.search_text,"
        + DatabaseSession.instantExpression "r.changed_at"
        + " AS changed_at,r.recorded_by"

    let private readWork name row : StoredWork =
        let id = caseId (name "case_id") row
        let revision = revision (name "revision") row
        let bytes = DatabaseSession.namedOptional<byte array> (name "snapshot") row
        let value = bytes |> Option.map (WorkRecord.decodeSnapshot >> parsed)
        let responsible = actorOption (name "responsible_actor") row
        let summary = DatabaseSession.namedOptional<string> (name "handling_summary") row
        let search = DatabaseSession.namedOptional<string> (name "search_text") row
        let expected = value |> Option.map _.Work

        if
            value
            |> Option.exists (fun value -> value.CaseId <> id || value.Revision <> revision)
        then
            raise (RecordCorruptException "Work identity disagrees.")

        if
            responsible <> (expected |> Option.bind _.Responsible)
            || summary
               <> (expected |> Option.bind _.Summary |> Option.map HandlingSummary.format)
            || search <> (summary |> Option.map CaseSearch.normalize)
        then
            raise (RecordCorruptException "Work columns disagree with snapshot.")

        {
            CaseId = id
            Revision = revision
            Snapshot = value
            SnapshotBytes = defaultArg bytes Array.empty
            ChangedAt =
                DatabaseSession.namedOptional<string> (name "changed_at") row
                |> Option.map (fun _ ->
                    DatabaseSession.namedRecordedInstant (name "changed_at") row)
            RecordedBy = actorOption (name "recorded_by") row
        }

    let private readReminder name row : StoredReminder =
        let id =
            DatabaseSession.namedValue<Guid> (name "reminder_id") row
            |> DatabaseSession.admitted ReminderId.create

        let parent = caseId (name "case_id") row
        let revision = revision (name "revision") row
        let bytes = DatabaseSession.namedOptional<byte array> (name "snapshot") row
        let value = bytes |> Option.map (ReminderRecord.decodeSnapshot >> parsed)
        let state = DatabaseSession.namedOptional<string> (name "state") row
        let action = DatabaseSession.namedOptional<string> (name "action") row

        let due =
            DatabaseSession.namedOptionalCalendarDate (name "due") row
            |> Option.map CalendarDate.ofDate

        let search = DatabaseSession.namedOptional<string> (name "search_text") row

        if
            value
            |> Option.exists (fun value ->
                Reminder.id value <> id
                || Reminder.caseId value <> parent
                || Reminder.revision value <> revision)
        then
            raise (RecordCorruptException "Reminder identity disagrees.")

        if
            state <> (value |> Option.map (Reminder.state >> ReminderState.token))
            || action <> (value |> Option.map (Reminder.action >> ReminderAction.format))
            || due <> (value |> Option.map Reminder.due)
            || search <> (action |> Option.map CaseSearch.normalize)
        then
            raise (RecordCorruptException "Reminder columns disagree with snapshot.")

        {
            ReminderId = id
            CaseId = parent
            Revision = revision
            Snapshot = value
            SnapshotBytes = defaultArg bytes Array.empty
            RecordedAt = DatabaseSession.namedRecordedInstant (name "changed_at") row
            RecordedBy = actor (name "recorded_by") row
        }

    let workRow row = readWork id row

    let reminderRow row = readReminder id row

    let findWork connection id token =
        task {
            let! rows =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {workColumns} FROM claimcore.case_work WHERE case_id=$1")
                    [| box (CaseId.value id) |]
                    token

            match rows with
            | [] -> return None
            | [ row ] -> return Some(workRow row)
            | _ -> return raise (RecordCorruptException "Duplicate work identity.")
        }

    let findReminder connection id token =
        task {
            let! rows =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {reminderColumns} FROM claimcore.reminders r WHERE r.reminder_id=$1")
                    [| box (ReminderId.value id) |]
                    token

            match rows with
            | [] -> return None
            | [ row ] -> return Some(reminderRow row)
            | _ -> return raise (RecordCorruptException "Duplicate reminder identity.")
        }

    let private workName name = "work_record_" + name
    let private nextName name = "next_" + name

    let summaryColumns =
        "w.case_id AS work_record_case_id,w.revision AS work_record_revision,w.snapshot AS work_record_snapshot,w.responsible_actor AS work_record_responsible_actor,w.handling_summary AS work_record_handling_summary,w.search_text AS work_record_search_text,"
        + DatabaseSession.instantExpression "w.changed_at"
        + " AS work_record_changed_at,w.recorded_by AS work_record_recorded_by,n.pending AS work_record_pending,n.overdue AS work_record_overdue,"
        + "r.reminder_id AS next_reminder_id,r.case_id AS next_case_id,r.revision AS next_revision,r.snapshot AS next_snapshot,r.state AS next_state,r.action AS next_action,"
        + DatabaseSession.dateExpression "r.due"
        + " AS next_due,r.search_text AS next_search_text,"
        + DatabaseSession.instantExpression "r.changed_at"
        + " AS next_changed_at,r.recorded_by AS next_recorded_by"

    /// Callers select the bounded parent set before joining current reminder aggregates.
    let summaryJoins dateParameter =
        $"CROSS JOIN LATERAL(SELECT count(*) AS pending,count(*) FILTER(WHERE due<{dateParameter}::date) AS overdue FROM claimcore.reminders WHERE case_id=w.case_id AND state='PENDING') n LEFT JOIN LATERAL(SELECT * FROM claimcore.reminders WHERE case_id=w.case_id AND state='PENDING' ORDER BY due,reminder_id LIMIT 1) r ON true"

    let summaryRow row =
        match DatabaseSession.namedOptional<Guid> (workName "case_id") row with
        | None -> None
        | Some _ ->
            let work = readWork workName row

            let next =
                match DatabaseSession.namedOptional<Guid> (nextName "reminder_id") row with
                | None -> None
                | Some _ ->
                    match (readReminder nextName row).Snapshot with
                    | Some value -> Some value
                    | None -> raise (RecordCorruptException "Current next reminder has no payload.")

            Some
                {
                    CaseWorkSummary.Work = work
                    Next = next
                    Pending = DatabaseSession.namedValue<int64> (workName "pending") row
                    Overdue = DatabaseSession.namedValue<int64> (workName "overdue") row
                }

    let summaries connection ids today token =
        task {
            if List.isEmpty ids then
                return Map.empty
            else
                let joins = summaryJoins "$2"

                let sql =
                    $"SELECT {summaryColumns} FROM claimcore.case_work w {joins} WHERE w.case_id=ANY($1::uuid[])"

                let! rows =
                    DatabaseSession.preparedNamedRows
                        connection
                        sql
                        [| box (ids |> List.map CaseId.value |> List.toArray); box today |]
                        token

                return
                    rows
                    |> List.map (fun row ->
                        let summary =
                            summaryRow row
                            |> Option.defaultWith (fun () ->
                                raise (RecordCorruptException "Work summary identity is absent."))

                        summary.Work.CaseId, summary)
                    |> Map.ofList
        }

    let accounts connection pairs token =
        task {
            if List.isEmpty pairs then
                return Map.empty
            else
                let! rows =
                    DatabaseSession.namedRows
                        connection
                        "SELECT p.case_id,a.actor_id,a.principal_kind,a.enabled,a.display_name,a.contact,(a.enabled AND a.principal_kind='HUMAN' AND EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=a.actor_id AND (g.case_id IS NULL OR g.case_id=p.case_id) AND g.role=ANY($3::text[])) AND EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=a.actor_id AND (g.case_id IS NULL OR g.case_id=p.case_id) AND g.role=ANY($4::text[]))) AS available FROM unnest($1::uuid[],$2::uuid[]) AS p(case_id,actor_id) JOIN claimcore.actors a USING(actor_id)"
                        [|
                            box (pairs |> List.map (fst >> CaseId.value) |> List.toArray)
                            box (pairs |> List.map (snd >> ActorId.value) |> List.toArray)
                            box (
                                Authority.rolesFor Capability.ReadCase
                                |> List.map DurableNames.role
                                |> List.toArray
                            )
                            box (
                                Authority.rolesFor Capability.ManageCaseWork
                                |> List.map DurableNames.role
                                |> List.toArray
                            )
                        |]
                        token

                return
                    rows
                    |> List.map (fun row ->
                        caseId "case_id" row,
                        ActorRef.create
                            (actor "actor_id" row)
                            (DurableNames.tryPrincipalKind (
                                DatabaseSession.namedValue<string> "principal_kind" row
                             )
                             |> Option.defaultWith (fun () ->
                                 raise (RecordCorruptException "Invalid assigned principal kind.")))
                            (if not (DatabaseSession.namedValue<bool> "enabled" row) then
                                 ActorAvailability.Disabled
                             elif DatabaseSession.namedValue<bool> "available" row then
                                 ActorAvailability.Available
                             else
                                 ActorAvailability.NotEligible)
                            (Projections.profile
                                (DatabaseSession.namedOptional<string> "display_name" row)
                                (DatabaseSession.namedOptional<string> "contact" row)))
                    |> Map.ofList
        }

    let private literalSearch value =
        value
        |> Option.map (fun value ->
            (CaseSearchText.value value)
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_"))

    let private duePredicate =
        function
        | ReminderDue.Overdue -> "r.due<$11::date"
        | ReminderDue.Today -> "r.due=$11::date"
        | ReminderDue.Upcoming -> "r.due>$11::date"

    let page connection (query: ReminderPageQuery) token =
        task {
            let due = query.Due |> Option.map duePredicate |> Option.defaultValue "TRUE"

            let view =
                query.View
                |> Option.map ViewCountStorage.reminderPredicate
                |> Option.defaultValue "TRUE"

            let sql =
                $"SELECT {reminderColumns},c.case_reference,c.state AS parent_lifecycle,c.status AS parent_status FROM claimcore.reminders r JOIN claimcore.cases c ON c.case_id=r.case_id WHERE {CaseSelectionSql.predicate query.Selection} AND r.snapshot IS NOT NULL AND ($12::uuid IS NULL OR r.case_id=$12) AND ($13::text IS NULL OR r.state=$13) AND ({view}) AND ($14 OR (c.state='ACTIVE' AND c.status='OPENED')) AND ({due}) AND ($15::text IS NULL OR r.search_text LIKE '%%'||$15||'%%' ESCAPE chr(92)) AND ($16::date IS NULL OR (r.due,r.reminder_id)>($16::date,$17::uuid)) ORDER BY r.due,r.reminder_id LIMIT $18"

            let parameters =
                Array.append
                    (CaseSelectionSql.parameters query.Selection)
                    [|
                        DatabaseSession.parameter (query.TargetCaseId |> Option.map CaseId.value)
                        DatabaseSession.parameter (
                            query.State |> Option.map DurableNames.reminderState
                        )
                        box query.IncludeInactive
                        DatabaseSession.parameter (literalSearch query.Text)
                        DatabaseSession.parameter (
                            query.After |> Option.map (fun p -> CalendarDate.format p.Due)
                        )
                        DatabaseSession.parameter (
                            query.After |> Option.map (fun p -> ReminderId.value p.ReminderId)
                        )
                        box (query.Limit + 1)
                    |]

            let! rows = DatabaseSession.namedRows connection sql parameters token

            let rows =
                rows
                |> List.map (fun row ->
                    {
                        ReminderRow.Stored = reminderRow row
                        Reference =
                            DatabaseSession.namedValue<string> "case_reference" row
                            |> CaseReference.parse
                            |> parsed
                        Lifecycle =
                            DatabaseSession.namedValue<string> "parent_lifecycle" row
                            |> Vocabulary.tryParse Lifecycle.vocabulary
                            |> Option.defaultWith (fun () ->
                                raise (RecordCorruptException "Invalid parent lifecycle."))
                        Status =
                            DatabaseSession.namedOptional<string> "parent_status" row
                            |> Option.map (fun value ->
                                DurableNames.tryStatus value
                                |> Option.defaultWith (fun () ->
                                    raise (RecordCorruptException "Invalid parent status.")))
                    })

            let visible = List.truncate query.Limit rows

            return
                {
                    Items = visible
                    Next =
                        if rows.Length > query.Limit then
                            visible
                            |> List.tryLast
                            |> Option.map (fun row ->
                                {
                                    ReminderPosition.Due =
                                        row.Stored.Snapshot.Value |> Reminder.due
                                    ReminderId = row.Stored.ReminderId
                                })
                        else
                            None
                }
        }

    let overview connection (selection: CaseSelection) mine token =
        task {
            let sql =
                $"WITH selected AS (SELECT c.case_id,c.responsible_actor,({CaseSelectionSql.viewPredicate CaseView.NeedsAttention}) AS attention FROM claimcore.cases c WHERE {CaseSelectionSql.predicate selection} AND c.state='ACTIVE' AND c.status='OPENED'), counts AS (SELECT r.case_id,count(*) AS pending,count(*) FILTER(WHERE r.due<$11::date) AS overdue FROM claimcore.reminders r JOIN selected s USING(case_id) WHERE r.state='PENDING' GROUP BY r.case_id), items AS (SELECT s.*,coalesce(n.pending,0) AS pending,coalesce(n.overdue,0) AS overdue,(s.responsible_actor IS NOT NULL AND EXISTS(SELECT 1 FROM claimcore.actors a WHERE a.actor_id=s.responsible_actor AND a.enabled AND a.principal_kind='HUMAN' AND EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=a.actor_id AND (g.case_id IS NULL OR g.case_id=s.case_id) AND g.role=ANY($12::text[])) AND EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=a.actor_id AND (g.case_id IS NULL OR g.case_id=s.case_id) AND g.role=ANY($13::text[])))) AS available FROM selected s LEFT JOIN counts n USING(case_id)) SELECT count(*) AS actionable,count(*) FILTER(WHERE pending=0) AS missing,count(*) FILTER(WHERE overdue>0) AS overdue_cases,coalesce(sum(overdue),0)::bigint AS overdue_reminders,count(*) FILTER(WHERE responsible_actor IS NULL) AS unassigned,count(*) FILTER(WHERE responsible_actor IS NOT NULL AND NOT available) AS unavailable,count(*) FILTER(WHERE attention) AS attention FROM items"

            let! rows =
                DatabaseSession.namedRows
                    connection
                    sql
                    (Array.append
                        (CaseSelectionSql.parameters selection)
                        [|
                            box (
                                Authority.rolesFor Capability.ReadCase
                                |> List.map DurableNames.role
                                |> List.toArray
                            )
                            box (
                                Authority.rolesFor Capability.ManageCaseWork
                                |> List.map DurableNames.role
                                |> List.toArray
                            )
                        |])
                    token

            let row = rows.Head

            let count name =
                DatabaseSession.namedValue<int64> name row

            return
                {
                    WorkOverview.Basis = selection.DateBasis
                    Mine = mine
                    ActionableCases = count "actionable"
                    MissingPlanCases = count "missing"
                    OverdueCases = count "overdue_cases"
                    OverdueReminders = count "overdue_reminders"
                    UnassignedCases = count "unassigned"
                    UnavailableAssigneeCases = count "unavailable"
                    AttentionCases = count "attention"
                }
        }
