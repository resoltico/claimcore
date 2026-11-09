namespace ClaimCore.Postgres

open System
open ClaimCore.Domain
open ClaimCore.Application

module internal NoteStorage =
    let columns =
        "n.note_id,n.case_id,n.created_sequence,n.text,l.subject_id,l.parent_case_id,l.act,l.subject_kind,"
        + DatabaseSession.instantExpression "l.recorded_at"
        + " AS recorded_at,l.actor_id"

    let source =
        " FROM claimcore.notes n JOIN claimcore.ledger l ON l.sequence=n.created_sequence"

    let row value : StoredNote =
        let id =
            DatabaseSession.namedValue<Guid> "note_id" value
            |> DatabaseSession.admitted NoteId.create

        let parent =
            DatabaseSession.namedValue<Guid> "case_id" value
            |> DatabaseSession.admitted CaseId.create

        if
            DatabaseSession.namedValue<Guid> "subject_id" value <> NoteId.value id
            || DatabaseSession.namedValue<Guid> "parent_case_id" value <> CaseId.value parent
            || DatabaseSession.namedValue<string> "act" value <> "ADD_NOTE"
            || DatabaseSession.namedValue<string> "subject_kind" value <> "NOTE"
        then
            raise (RecordCorruptException "Note creation source disagrees.")

        let text =
            DatabaseSession.namedOptional<string> "text" value
            |> Option.map (fun raw ->
                NoteText.parse raw
                |> Result.defaultWith (fun _ ->
                    raise (RecordCorruptException "Invalid note text.")))

        {
            Note = Note.ofAccepted id parent text
            CreatedPosition =
                DatabaseSession.namedValue<int64> "created_sequence" value
                |> DatabaseSession.admitted Sequence.create
            RecordedAt = DatabaseSession.namedRecordedInstant "recorded_at" value
            RecordedBy =
                DatabaseSession.namedValue<Guid> "actor_id" value
                |> DatabaseSession.admitted ActorId.create
        }

    let find connection id token =
        task {
            let! rows =
                DatabaseSession.namedRows
                    connection
                    ("SELECT " + columns + source + " WHERE n.note_id=$1")
                    [| box (NoteId.value id) |]
                    token

            match rows with
            | [] -> return None
            | [ value ] -> return Some(row value)
            | _ -> return raise (RecordCorruptException "Duplicate note identity.")
        }

    let page connection (query: NotePageQuery) token =
        task {
            let parent = box (CaseId.value query.CaseId)

            let! count =
                DatabaseSession.rows
                    connection
                    "SELECT count(*) FROM claimcore.notes WHERE case_id=$1 AND text IS NOT NULL"
                    [| parent |]
                    token

            let! rows =
                DatabaseSession.namedRows
                    connection
                    ("SELECT "
                     + columns
                     + source
                     + " WHERE n.case_id=$1 AND n.text IS NOT NULL AND ($2::bigint IS NULL OR n.created_sequence<$2) ORDER BY n.created_sequence DESC LIMIT $3")
                    [|
                        parent
                        query.Before
                        |> Option.map (Sequence.value >> box)
                        |> Option.defaultValue (box DBNull.Value)
                        box (query.Limit + 1)
                    |]
                    token

            let items = rows |> List.truncate query.Limit |> List.map row

            return
                {
                    Items = items
                    Count = DatabaseSession.value<int64> 0 count.Head
                    Next =
                        if rows.Length > query.Limit then
                            items |> List.tryLast |> Option.map _.CreatedPosition
                        else
                            None
                }
        }
