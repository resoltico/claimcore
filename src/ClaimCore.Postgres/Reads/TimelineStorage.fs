namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

module internal TimelineStorage =
    let private disposition connection installation caseId act token =
        task {
            match act with
            | CaseActKind.Void
            | CaseActKind.Reinstate ->
                let! rows =
                    DatabaseSession.rows
                        connection
                        ("SELECT "
                         + StoredEntries.columns
                         + " FROM claimcore.ledger WHERE subject_kind='CASE' AND subject_id=$1 ORDER BY case_revision DESC LIMIT 1")
                        [| box (CaseId.value caseId) |]
                        token

                match rows with
                | [ row ] ->
                    let stored = StoredEntries.decode installation row

                    if LedgerEntry.hash stored.Metadata <> stored.Entry.EntryHash then
                        return
                            raise (RecordCorruptException "Invalid current disposition entry hash.")
                    else
                        return Some stored.Entry
                | _ -> return raise (RecordCorruptException "Current disposition is absent.")
            | _ -> return None
        }

    let stages connection installation caseId token =
        task {
            let opened =
                DatabaseSession.dateExpression
                    "(SELECT business_date FROM claimcore.ledger o WHERE o.subject_kind='CASE' AND o.subject_id=$1 AND o.case_revision=1 AND o.act='OPEN')"

            let closed =
                DatabaseSession.dateExpression
                    "(SELECT business_date FROM claimcore.ledger c WHERE c.subject_kind='CASE' AND c.subject_id=$1 AND c.act='CLOSE' ORDER BY c.case_revision DESC LIMIT 1)"

            let recorded = DatabaseSession.instantExpression "recorded_at"

            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT case_revision,act,{opened},{closed},{recorded},actor_id FROM claimcore.ledger WHERE subject_kind='CASE' AND subject_id=$1 ORDER BY case_revision DESC LIMIT 1")
                    [| box (CaseId.value caseId) |]
                    token

            match rows with
            | [] -> return None
            | [ row ] ->
                let lastAct =
                    DatabaseSession.value<string> 1 row
                    |> Vocabulary.tryParse CaseActKind.vocabulary
                    |> Option.defaultWith (fun () ->
                        raise (RecordCorruptException "Invalid last case stage act."))

                let! disposition = disposition connection installation caseId lastAct token

                return
                    Some
                        {
                            CaseStageFacts.Revision =
                                DatabaseSession.value<int64> 0 row
                                |> DatabaseSession.admitted Revision.create
                            LastAct = lastAct
                            DispositionEntry = disposition
                            RecordedAt = DatabaseSession.recordedInstant 4 row
                            RecordedBy =
                                DatabaseSession.value<System.Guid> 5 row
                                |> DatabaseSession.admitted ActorId.create
                            OpenedOn = DatabaseSession.calendarDate 2 row |> CalendarDate.ofDate
                            ClosedOn =
                                DatabaseSession.optionalCalendarDate 3 row
                                |> Option.map CalendarDate.ofDate
                        }
            | _ -> return raise (RecordCorruptException "Duplicate current case stage.")
        }

    let page connection installation (query: TimelinePageQuery) token =
        task {
            let filter =
                match query.Filter with
                | None -> "TRUE"
                | Some TimelineFilter.Notes -> "subject_kind='NOTE'"
                | Some TimelineFilter.Facts -> "subject_kind='CASE'"
                | Some TimelineFilter.Work -> "subject_kind IN ('WORK','REMINDER')"

            let entity, expected, actual, reminder =
                match query.Range with
                | None -> None, 0L, 0L, None
                | Some range ->
                    let kind, id =
                        match range.Entity with
                        | ChangeEntity.Case -> "CASE", None
                        | ChangeEntity.Work -> "WORK", None
                        | ChangeEntity.Reminder id -> "REMINDER", Some(ReminderId.value id)

                    Some kind,
                    Revision.value range.ExpectedRevision,
                    Revision.value range.ActualRevision,
                    id

            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {StoredEntries.columns} FROM claimcore.ledger WHERE ((subject_kind='CASE' AND subject_id=$1) OR (subject_kind IN ('WORK','REMINDER','NOTE') AND parent_case_id=$1)) AND ({filter}) AND ($2::bigint IS NULL OR sequence<$2) AND ($4::text IS NULL OR (subject_kind=$4 AND case_revision>$5 AND case_revision<=$6 AND ($4<>'REMINDER' OR subject_id=$7)) OR ($4='REMINDER' AND subject_kind='REMINDER' AND next_reminder_id=$7 AND $5<1 AND $6>=1)) ORDER BY sequence DESC LIMIT $3")
                    [|
                        box (CaseId.value query.CaseId)
                        DatabaseSession.parameter (
                            query.BeforePosition |> Option.map Sequence.value
                        )
                        box (query.Limit + 1)
                        DatabaseSession.parameter entity
                        box expected
                        box actual
                        DatabaseSession.parameter reminder
                    |]
                    token

            let entries = rows |> List.map (StoredEntries.decode installation >> _.Entry)
            let items = List.truncate query.Limit entries

            return
                {
                    Items = items
                    Next =
                        if entries.Length > query.Limit then
                            items |> List.tryLast |> Option.map _.Sequence
                        else
                            None
                }
        }

    let private target (entry: StoredEffect) =
        match entry.Subject with
        | EntryTarget.Case(id, revision, _, _) -> "CASE", CaseId.value id, Revision.value revision
        | EntryTarget.Work work -> "WORK", CaseId.value work.CaseId, Revision.value work.Revision
        | EntryTarget.Note note ->
            "NOTE", NoteId.value note.NoteId, (if note.Act = NoteActKind.AddNote then 1L else 2L)
        | EntryTarget.Reminder reminder ->
            "REMINDER", ReminderId.value reminder.ReminderId, Revision.value reminder.Revision
        | EntryTarget.Authority _ -> invalidArg (nameof entry) "Use case timeline entries."

    /// One bounded query loads each visible entry's immediate entity predecessor, including a reminder born in complete-and-plan-next.
    let previous connection installation (entries: StoredEffect list) token =
        task {
            if entries.Length > Paging.MaximumPageSize then
                invalidArg (nameof entries) "Use at most one admitted timeline page."

            if List.isEmpty entries then
                return Map.empty
            else
                let targets = entries |> List.map target

                let! rows =
                    DatabaseSession.rows
                        connection
                        ($"SELECT matched.*,requested_seq FROM unnest($1::bigint[],$2::text[],$3::uuid[],$4::bigint[]) AS requested(requested_seq,requested_kind,requested_id,requested_revision) CROSS JOIN LATERAL (SELECT {StoredEntries.columns} FROM claimcore.ledger WHERE sequence<requested_seq AND subject_kind=requested_kind AND subject_id=requested_id AND ((requested_kind='NOTE' AND requested_revision=2 AND act='ADD_NOTE') OR (requested_kind<>'NOTE' AND case_revision=requested_revision-1)) UNION ALL SELECT {StoredEntries.columns} FROM claimcore.ledger WHERE requested_kind='REMINDER' AND requested_revision=2 AND subject_kind='REMINDER' AND next_reminder_id=requested_id AND sequence<requested_seq) matched")
                        [|
                            box (
                                entries
                                |> List.map (fun entry -> Sequence.value entry.Sequence)
                                |> List.toArray
                            )
                            box (targets |> List.map (fun (kind, _, _) -> kind) |> List.toArray)
                            box (targets |> List.map (fun (_, id, _) -> id) |> List.toArray)
                            box (
                                targets
                                |> List.map (fun (_, _, revision) -> revision)
                                |> List.toArray
                            )
                        |]
                        token

                let predecessors =
                    rows
                    |> List.map (fun row ->
                        let requested =
                            DatabaseSession.value<int64> (row.Length - 1) row
                            |> DatabaseSession.admitted Sequence.create

                        requested, (StoredEntries.decode installation row).Entry)

                if
                    predecessors |> List.map fst |> List.distinct |> List.length
                    <> predecessors.Length
                then
                    raise (
                        RecordCorruptException
                            "A timeline entity has duplicate predecessor identities."
                    )

                return Map.ofList predecessors
        }
