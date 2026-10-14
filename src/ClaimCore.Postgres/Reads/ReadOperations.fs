namespace ClaimCore.Postgres

open System
open ClaimCore.Application

module internal ReadOperations =
    let private instant connection token =
        task {
            let! rows = DatabaseSession.rows connection "SELECT clock_timestamp()" [||] token
            return DatabaseSession.instant 0 rows.Head
        }

    let actor source principal token =
        Transactions.read source token (fun connection ->
            task {
                let! instant = instant connection token
                let! actor = Projections.actor connection principal token
                return { ObservedAt = instant; Actor = actor }
            })

    let case source installationId principal operationId reference token =
        Transactions.read source token (fun connection ->
            task {
                let! instant = instant connection token
                let! actor = Projections.actor connection principal token

                let! existing =
                    match operationId with
                    | Some id -> StoredEntries.operation connection installationId id token
                    | None -> Threading.Tasks.Task.FromResult None

                let! case = Projections.caseBy connection "case_reference" (box reference) token

                return
                    {
                        ObservedAt = instant
                        Actor = actor
                        ExistingOperation = existing
                        Case = case
                    }
            })

    let operation source installationId principal id token =
        Transactions.read source token (fun connection ->
            task {
                let! actor = Projections.actor connection principal token
                let! operation = StoredEntries.operation connection installationId id token

                let! case =
                    match operation |> Option.map _.Scope with
                    | Some(StoredScope.Case(id, _, _, _)) ->
                        Projections.caseBy connection "case_id" (box id) token
                    | _ -> Threading.Tasks.Task.FromResult None

                return actor, operation, case
            })

    let list source principal visibility after limit token =
        Transactions.read source token (fun connection ->
            task {
                let! actor = Projections.actor connection principal token

                match actor |> Option.bind visibility with
                | None -> return None
                | Some scope ->
                    let ids =
                        match scope with
                        | CaseVisibility.Every -> None
                        | CaseVisibility.Only ids -> Some(Array.ofList ids)

                    let! rows =
                        DatabaseSession.rows
                            connection
                            ($"SELECT {Projections.caseColumns} FROM claimcore.cases WHERE ($1::text IS NULL OR case_reference > $1 COLLATE \"C\") AND ($2::uuid[] IS NULL OR case_id=ANY($2)) ORDER BY case_reference COLLATE \"C\" LIMIT $3")
                            [|
                                DatabaseSession.parameter after
                                DatabaseSession.parameter ids
                                box (limit + 1)
                            |]
                            token

                    let items = rows |> List.truncate limit |> List.map Projections.caseRow

                    return
                        Some
                            {
                                Actor = actor
                                Items = items
                                NextAfterReference =
                                    if rows.Length > limit then
                                        items |> List.tryLast |> Option.map _.Reference
                                    else
                                        None
                            }
            })

    let history source installationId principal reference after limit token =
        Transactions.read source token (fun connection ->
            task {
                let! actor = Projections.actor connection principal token
                let! case = Projections.caseBy connection "case_reference" (box reference) token

                match case with
                | None ->
                    return
                        {
                            Actor = actor
                            Case = None
                            Entries = []
                            NextAfterRevision = None
                        }
                | Some case ->
                    let! rows =
                        DatabaseSession.rows
                            connection
                            ($"SELECT {StoredEntries.columns} FROM claimcore.ledger WHERE case_id=$1 AND case_revision>$2 ORDER BY case_revision LIMIT $3")
                            [| box case.CaseId; box after; box (limit + 1) |]
                            token

                    let entries =
                        rows
                        |> List.truncate limit
                        |> List.map (StoredEntries.decode installationId Guid.Empty >> _.Entry)

                    let next =
                        if rows.Length > limit then
                            entries
                            |> List.tryLast
                            |> Option.map (fun entry ->
                                match entry.Scope with
                                | StoredScope.Case(_, rev, _, _) -> rev
                                | _ -> invalidOp "Invalid case history.")
                        else
                            None

                    return
                        {
                            Actor = actor
                            Case = Some case
                            Entries = entries
                            NextAfterRevision = next
                        }
            })
