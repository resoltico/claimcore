namespace ClaimCore.Postgres

open System
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal ReadOperations =
    let private instant connection token =
        task {
            let! rows = DatabaseSession.rows connection "SELECT clock_timestamp()" [||] token
            return DatabaseSession.instant 0 rows.Head
        }

    let actor source principal token =
        Transactions.read source token (fun connection ->
            task {
                let! time = instant connection token
                let! actor = Projections.actor connection principal token
                return ({ ObservedAt = time; Actor = actor }: ActorReadView)
            })

    let case source installationId principal operationId reference token =
        Transactions.read source token (fun connection ->
            task {
                let! time = instant connection token
                let! actor = Projections.actor connection principal token

                let! existing =
                    match operationId with
                    | Some id -> StoredEntries.operation connection installationId id token
                    | None -> Task.FromResult None

                let! record =
                    Projections.caseById
                        connection
                        (CaseReferenceId.encode installationId reference)
                        token

                return
                    ({
                        ObservedAt = time
                        Actor = actor
                        ExistingOperation = existing
                        Case = record
                    }
                    : CaseReadView)
            })

    let operation source installationId principal id token =
        Transactions.read source token (fun connection ->
            task {
                let! time = instant connection token
                let! actor = Projections.actor connection principal token
                let! entry = StoredEntries.operation connection installationId id token

                let! record =
                    match entry |> Option.map _.Scope with
                    | Some(StoredScope.Case(id, _, _, _)) ->
                        Projections.caseById connection id token
                    | _ -> Task.FromResult None

                return
                    ({
                        ObservedAt = time
                        Actor = actor
                        Entry = entry
                        Case = record
                    }
                    : OperationReadView)
            })

    let list source principal select limit token =
        Transactions.read source token (fun connection ->
            task {
                let! time = instant connection token
                let! actor = Projections.actor connection principal token
                let view: ActorReadView = { ObservedAt = time; Actor = actor }

                match select view with
                | Error reason -> return Error reason
                | Ok selection ->
                    let ids =
                        match selection.Visibility with
                        | CaseVisibility.Every -> None
                        | CaseVisibility.Only ids -> Some(Array.ofList ids)

                    let! rows =
                        DatabaseSession.rows
                            connection
                            ($"SELECT {Projections.caseColumns} FROM claimcore.cases WHERE ($1::text IS NULL OR upper(case_reference COLLATE \"C\") > $1 COLLATE \"C\") AND ($2::uuid[] IS NULL OR case_id=ANY($2)) ORDER BY upper(case_reference COLLATE \"C\") LIMIT $3")
                            [|
                                DatabaseSession.parameter selection.AfterReference
                                DatabaseSession.parameter ids
                                box (limit + 1)
                            |]
                            token

                    let items = rows |> List.truncate limit |> List.map Projections.caseRow

                    let next =
                        if rows.Length > limit then
                            items
                            |> List.tryLast
                            |> Option.map (fun record ->
                                CaseReference.key record.Snapshot.Reference)
                        else
                            None

                    return
                        Ok
                            {
                                ObservedAt = time
                                Actor = actor
                                Items = items
                                NextAfterReference = next
                            }
            })

    let private historyRows
        connection
        installationId
        (record: StoredCase option)
        after
        limit
        token
        =
        task {
            match record with
            | None -> return []
            | Some record ->
                let! rows =
                    DatabaseSession.rows
                        connection
                        ($"SELECT {StoredEntries.columns} FROM claimcore.ledger WHERE scope='CASE' AND subject_id=$1 AND case_revision>$2 ORDER BY case_revision LIMIT $3")
                        [| box record.CaseId; box after; box (limit + 1) |]
                        token

                return rows |> List.map (StoredEntries.decode installationId >> _.Entry)
        }

    let private nextRevision rows visible limit =
        if List.length rows <= limit then
            None
        else
            visible
            |> List.tryLast
            |> Option.map (fun (entry: StoredEntry) ->
                match entry.Scope with
                | StoredScope.Case(_, revision, _, _) -> revision
                | _ -> invalidOp "Invalid case history.")

    let history source installationId principal reference select limit token =
        Transactions.read source token (fun connection ->
            task {
                let! time = instant connection token
                let! actor = Projections.actor connection principal token

                let! record =
                    Projections.caseById
                        connection
                        (CaseReferenceId.encode installationId reference)
                        token

                let view: HistoryReadView =
                    {
                        ObservedAt = time
                        Actor = actor
                        Case = record
                    }

                match select view with
                | Error reason -> return Error reason
                | Ok after ->
                    let! rows = historyRows connection installationId record after limit token
                    let visible = rows |> List.truncate limit

                    return
                        Ok
                            {
                                ObservedAt = time
                                Actor = actor
                                Case = record
                                Entries = visible
                                NextAfterRevision = nextRevision rows visible limit
                            }
            })

    let actors source principal select limit token =
        Transactions.read source token (fun connection ->
            task {
                let! time = instant connection token
                let! actor = Projections.actor connection principal token
                let view: ActorReadView = { ObservedAt = time; Actor = actor }

                match select view with
                | Error reason -> return Error reason
                | Ok after ->
                    let! rows =
                        DatabaseSession.rows
                            connection
                            "SELECT actor_id,principal_kind,issuer,subject,enabled FROM claimcore.actors WHERE ($1::uuid IS NULL OR actor_id>$1) ORDER BY actor_id LIMIT $2"
                            [| DatabaseSession.parameter after; box (limit + 1) |]
                            token

                    let items = rows |> List.truncate limit |> List.map Projections.actorSummary

                    let next =
                        if rows.Length > limit then
                            items |> List.tryLast |> Option.map _.ActorId
                        else
                            None

                    return
                        Ok
                            {
                                ObservedAt = time
                                Actor = actor
                                Items = items
                                NextAfterActorId = next
                            }
            })

    let grantPageSql =
        "SELECT g.role,c.case_reference,g.case_id FROM claimcore.grants g LEFT JOIN claimcore.cases c USING(case_id) WHERE g.actor_id=$1 AND ($2::text IS NULL OR g.role>$2 OR (g.role=$2 AND $3::uuid IS NOT NULL AND (g.case_id>$3 OR g.case_id IS NULL))) ORDER BY g.role ASC,g.case_id ASC NULLS LAST LIMIT $4"

    let private grantTarget connection targetId token =
        task {
            let! headers =
                DatabaseSession.rows
                    connection
                    "SELECT actor_id,principal_kind,issuer,subject,enabled FROM claimcore.actors WHERE actor_id=$1"
                    [| box targetId |]
                    token

            return headers |> List.tryExactlyOne |> Option.map Projections.actorSummary
        }

    let private grantRows connection targetId (after: GrantPosition option) limit token =
        task {
            return!
                DatabaseSession.rows
                    connection
                    grantPageSql
                    [|
                        box targetId
                        DatabaseSession.parameter (
                            after |> Option.map (fun p -> Roles.token p.Role)
                        )
                        DatabaseSession.parameter (after |> Option.bind _.CaseId)
                        box (limit + 1)
                    |]
                    token
        }

    let grants
        source
        principal
        targetId
        (select: GrantReadView -> Result<GrantPosition option, Rejection>)
        limit
        token
        =
        Transactions.read source token (fun connection ->
            task {
                let! time = instant connection token
                let! actor = Projections.actor connection principal token

                let! target = grantTarget connection targetId token

                let view: GrantReadView =
                    {
                        ObservedAt = time
                        Actor = actor
                        Target = target
                    }

                match select view with
                | Error reason -> return Error reason
                | Ok after ->
                    let! rows = grantRows connection targetId after limit token

                    let visible = rows |> List.truncate limit |> List.map Projections.grantRow

                    return
                        Ok
                            {
                                ObservedAt = time
                                Actor = actor
                                Target =
                                    target
                                    |> Option.defaultWith (fun () ->
                                        invalidOp "Missing selected grant target.")
                                Items = visible |> List.map fst
                                NextAfterGrant =
                                    if rows.Length > limit then
                                        visible |> List.tryLast |> Option.map snd
                                    else
                                        None
                            }
            })
