namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

module internal TimelineStorage =
    let stages connection installation parent token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT l.operation_id,e.effect_index FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence WHERE e.subject_kind='CASE' AND e.subject_id=$1 ORDER BY e.case_revision DESC LIMIT 1"
                    [| box (CaseId.value parent) |]
                    token

            let! effects = StoredEntries.bind connection installation rows token

            match effects with
            | [] -> return None
            | [ effect ] ->
                let revision, act =
                    match effect.Subject with
                    | EntryTarget.Case(_, revision, act, _) -> revision, act
                    | _ -> raise (RecordCorruptException "Invalid case stage effect.")

                let! dates =
                    DatabaseSession.rows
                        connection
                        ("SELECT "
                         + DatabaseSession.dateExpression
                             "(SELECT l.business_date FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence WHERE e.subject_kind='CASE' AND e.subject_id=$1 AND e.act='OPEN' ORDER BY e.operation_sequence,e.effect_index LIMIT 1)"
                         + ","
                         + DatabaseSession.dateExpression
                             "(SELECT l.business_date FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence WHERE e.subject_kind='CASE' AND e.subject_id=$1 AND e.act='CLOSE' ORDER BY e.operation_sequence DESC,e.effect_index DESC LIMIT 1)")
                        [| box (CaseId.value parent) |]
                        token

                let date = List.exactlyOne dates

                return
                    Some
                        {
                            CaseStageFacts.Revision = revision
                            OpenedOn = DatabaseSession.calendarDate 0 date |> CalendarDate.ofDate
                            ClosedOn =
                                DatabaseSession.optionalCalendarDate 1 date
                                |> Option.map CalendarDate.ofDate
                            LastAct = act
                            RecordedAt = effect.RecordedAt
                            RecordedBy = effect.ActorId
                            DispositionEntry =
                                if act = CaseActKind.Void || act = CaseActKind.Reinstate then
                                    Some effect
                                else
                                    None
                        }
            | _ -> return raise (RecordCorruptException "Duplicate current case stage.")
        }

    let page connection installation (query: TimelinePageQuery) token =
        task {
            let filter =
                match query.Filter with
                | None -> "TRUE"
                | Some TimelineFilter.Notes -> "e.subject_kind='NOTE'"
                | Some TimelineFilter.Facts -> "e.subject_kind='CASE'"
                | Some TimelineFilter.Work -> "e.subject_kind IN ('WORK','REMINDER')"

            let kind, expected, actual, reminder =
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
                    ($"SELECT l.operation_id,e.effect_index FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence WHERE ((e.subject_kind='CASE' AND e.subject_id=$1) OR e.parent_case_id=$1) AND ({filter}) AND ($2::bigint IS NULL OR (e.operation_sequence,e.effect_index)<($2::bigint,$3::integer)) AND ($5::text IS NULL OR (e.subject_kind=$5 AND e.case_revision>$6 AND e.case_revision<=$7 AND ($5<>'REMINDER' OR e.subject_id=$8))) ORDER BY e.operation_sequence DESC,e.effect_index DESC LIMIT $4")
                    [|
                        box (CaseId.value query.CaseId)
                        DatabaseSession.parameter (
                            query.BeforePosition
                            |> Option.map (EffectPosition.sequence >> Sequence.value)
                        )
                        DatabaseSession.parameter (
                            query.BeforePosition |> Option.map EffectPosition.index
                        )
                        box (query.Limit + 1)
                        DatabaseSession.parameter kind
                        box expected
                        box actual
                        DatabaseSession.parameter reminder
                    |]
                    token

            let! entries = StoredEntries.bind connection installation rows token
            let items = List.truncate query.Limit entries

            return
                {
                    Items = items
                    Next =
                        if entries.Length > query.Limit then
                            items |> List.tryLast |> Option.map _.Position
                        else
                            None
                }
        }

    /// Correlated lexicographic predecessor lookup includes earlier effects within the same root.
    let previous connection installation (entries: StoredEffect list) token =
        task {
            if entries.Length > Paging.MaximumTimelinePageSize then
                invalidArg (nameof entries) "Use one bounded timeline page."

            let mutable result = Map.empty

            for entry in entries do
                let kind, target =
                    match entry.Subject with
                    | EntryTarget.Case(id, _, _, _) -> "CASE", CaseId.value id
                    | EntryTarget.Work value -> "WORK", CaseId.value value.CaseId
                    | EntryTarget.Note value -> "NOTE", NoteId.value value.NoteId
                    | EntryTarget.Reminder value -> "REMINDER", ReminderId.value value.ReminderId
                    | _ -> invalidArg (nameof entries) "Use case activity effects."

                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT l.operation_id,e.effect_index FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence WHERE e.subject_kind=$1 AND e.subject_id=$2 AND (e.operation_sequence,e.effect_index)<($3,$4) ORDER BY e.operation_sequence DESC,e.effect_index DESC LIMIT 1"
                        [|
                            box kind
                            box target
                            box (Sequence.value entry.Sequence)
                            box entry.Index
                        |]
                        token

                let! found = StoredEntries.bind connection installation rows token

                match found with
                | [] -> ()
                | [ previous ] -> result <- Map.add entry.Position previous result
                | _ -> raise (RecordCorruptException "Duplicate immediate predecessor.")

            return result
        }
