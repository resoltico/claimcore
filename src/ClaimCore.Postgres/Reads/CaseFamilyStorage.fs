namespace ClaimCore.Postgres

open ClaimCore.Domain

/// Erasure loads complete related state; ordinary changes retain their scoped loaders.
module internal CaseFamilyStorage =
    let private owned value =
        value
        |> Result.defaultWith (fun _ ->
            raise (RecordCorruptException "Invalid owned case-family state."))

    let private index key values =
        let indexed = values |> List.map (fun value -> key value, value) |> Map.ofList

        if indexed.Count <> values.Length then
            raise (RecordCorruptException "Duplicate case-family rows.")

        indexed

    let load connection caseId token =
        task {
            let! parents =
                DatabaseSession.rows
                    connection
                    "SELECT state FROM claimcore.cases WHERE case_id=$1"
                    [| box (CaseId.value caseId) |]
                    token

            match parents with
            | [] -> return None
            | [ parent ] ->
                let lifecycle =
                    DatabaseSession.value<string> 0 parent
                    |> DatabaseSession.admitted (Vocabulary.tryParse Lifecycle.vocabulary)

                let parameters = [| box (CaseId.value caseId) |]

                let! work =
                    DatabaseSession.namedRows
                        connection
                        ($"SELECT {WorkStorage.workColumns} FROM claimcore.case_work WHERE case_id=$1")
                        parameters
                        token

                let work =
                    match work with
                    | [] -> None
                    | [ row ] ->
                        row
                        |> WorkStorage.workProjectionRow
                        |> WorkSubject.restore caseId lifecycle
                        |> owned
                        |> Some
                    | _ -> raise (RecordCorruptException "Duplicate case-work roots.")

                let! reminders =
                    DatabaseSession.namedRows
                        connection
                        ($"SELECT {WorkStorage.reminderColumns} FROM claimcore.reminders r WHERE r.case_id=$1")
                        parameters
                        token

                let! notes =
                    DatabaseSession.namedRows
                        connection
                        ("SELECT "
                         + NoteStorage.columns
                         + NoteStorage.source
                         + " WHERE n.case_id=$1")
                        parameters
                        token

                let reminders =
                    reminders
                    |> List.map (
                        WorkStorage.reminderProjectionRow
                        >> ReminderSubject.restore caseId lifecycle
                        >> owned
                    )
                    |> index _.ReminderId

                let notes =
                    notes
                    |> List.map (
                        NoteStorage.projectionRow >> NoteSubject.restore caseId lifecycle >> owned
                    )
                    |> index _.NoteId

                return
                    Some
                        {
                            CaseFamilyState.Work = work
                            Reminders = reminders
                            Notes = notes
                        }
            | _ -> return raise (RecordCorruptException "Duplicate case identity rows.")
        }
