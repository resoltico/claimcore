namespace ClaimCore.Postgres

open ClaimCore.RecordFormat
open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Domain
open Npgsql

module internal TransactionPages =
    let grantPageSql =
        "SELECT g.role,c.case_reference,g.case_id FROM claimcore.grants g LEFT JOIN claimcore.cases c USING(case_id) WHERE g.actor_id=$1 AND ($2::text IS NULL OR g.role>$2 OR (g.role=$2 AND $3::uuid IS NOT NULL AND (g.case_id>$3 OR g.case_id IS NULL))) ORDER BY g.role ASC,g.case_id ASC NULLS LAST LIMIT $4"

    let page limit position rows =
        let visible = List.truncate limit rows

        {
            Items = visible
            Next =
                if List.length rows > limit then
                    List.tryLast visible |> Option.map position
                else
                    None
        }

    let private caseSql order =
        let key = "upper(c.case_reference COLLATE \"C\")"

        let after, sorting =
            match order with
            | CaseListOrder.Reference -> key + " > $1 COLLATE \"C\"", key + " ASC"
            | CaseListOrder.Recent ->
                "(c.changed_at < $11 OR (c.changed_at = $11 AND "
                + key
                + " > $1 COLLATE \"C\"))",
                "c.changed_at DESC," + key + " ASC"

        let search =
            "($10::text IS NULL OR c.search_text LIKE '%' || $10 || '%' ESCAPE chr(92))"

        $"SELECT {Projections.caseColumns} FROM claimcore.cases c WHERE ($1::text IS NULL OR {after}) AND ($2 OR c.case_id IN (SELECT g.case_id FROM claimcore.grants g WHERE g.actor_id=$3 AND g.role=ANY($4::text[]) AND g.case_id IS NOT NULL)) AND NOT EXISTS(SELECT 1 FROM claimcore.grants bad WHERE bad.actor_id=$3 AND bad.case_id=c.case_id AND bad.role=ANY($5::text[])) AND (cardinality($7::text[])=0 OR c.state=ANY($7::text[])) AND (cardinality($8::text[])=0 OR c.status=ANY($8::text[])) AND (cardinality($9::text[])=0 OR c.progress=ANY($9::text[])) AND {search} ORDER BY {sorting} LIMIT $6"

    let private afterPosition (query: CasePageQuery) =
        match query.Filter.Order, query.AfterPosition with
        | _, None -> None, None
        | CaseListOrder.Reference, Some(CaseListPosition.Reference reference) ->
            Some reference, None
        | CaseListOrder.Recent, Some(CaseListPosition.Recent(changedAt, reference)) ->
            Some reference, Some changedAt
        | _ -> invalidArg (nameof query) "The page position must match its order."

    let private escapeSearch (value: string) =
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")

    let cases connection installation (query: CasePageQuery) token =
        let after, changedAt = afterPosition query
        let filter = query.Filter

        let parameters =
            [|
                DatabaseSession.parameter after
                box (query.Visibility = CaseVisibility.Every)
                box (ActorId.value query.ActorId)
                box (query.ReadRoles |> List.map DurableNames.role |> List.toArray)
                box (query.ForbiddenRoles |> List.map DurableNames.role |> List.toArray)
                box (query.Limit + 1)
                box (filter.Lifecycles |> List.map DurableNames.lifecycle |> List.toArray)
                box (filter.Statuses |> List.map DurableNames.status |> List.toArray)
                box (filter.Progress |> List.map DurableNames.progress |> List.toArray)
                DatabaseSession.parameter (
                    filter.Text |> Option.map (CaseSearchText.value >> escapeSearch)
                )
            |]

        let parameters =
            if filter.Order = CaseListOrder.Recent then
                Array.append parameters [| DatabaseSession.parameter changedAt |]
            else
                parameters

        task {
            let! rows =
                DatabaseSession.namedRows connection (caseSql filter.Order) parameters token

            return
                rows
                |> List.map (Projections.caseRow installation)
                |> page query.Limit (fun record ->
                    CaseListPosition.create
                        filter.Order
                        record.Index.ChangedAt
                        (CaseReference.format record.Snapshot.Reference))
        }

    let history connection installation (query: HistoryPageQuery) token =
        let id = query.CaseId
        let after = query.AfterRevision
        let limit = query.Limit

        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {StoredEntries.columns} FROM claimcore.ledger WHERE subject_kind='CASE' AND subject_id=$1 AND case_revision>$2 ORDER BY case_revision LIMIT $3")
                    [| box (CaseId.value id); box (Revision.value after); box (limit + 1) |]
                    token

            let entries = rows |> List.map (StoredEntries.decode installation >> _.Entry)

            let revision (entry: StoredEntry) =
                match entry.Subject with
                | EntryTarget.Case(_, revision, _, _) -> revision
                | _ -> raise (RecordCorruptException "Invalid case history.")

            return page limit revision entries
        }

    let actors connection after limit token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT actor_id,principal_kind,issuer,subject,enabled FROM claimcore.actors WHERE ($1::uuid IS NULL OR actor_id>$1) ORDER BY actor_id LIMIT $2"
                    [|
                        DatabaseSession.parameter (after |> Option.map ActorId.value)
                        box (limit + 1)
                    |]
                    token

            return rows |> List.map Projections.actorRow |> page limit (fun actor -> actor.ActorId)
        }

    let grants connection target (after: GrantPosition option) limit token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    grantPageSql
                    [|
                        box (ActorId.value target)
                        DatabaseSession.parameter (
                            after |> Option.map (fun value -> DurableNames.role value.Role)
                        )
                        DatabaseSession.parameter (
                            after |> Option.bind _.CaseId |> Option.map CaseId.value
                        )
                        box (limit + 1)
                    |]
                    token

            let visible = rows |> List.map Projections.grantRow |> page limit snd

            return
                {
                    Items = visible.Items |> List.map fst
                    Next = visible.Next
                }
        }

/// An instance is scoped to one provider transaction; it contains no operation callbacks.
type private ReadTx
    (connection: NpgsqlConnection, installation, observedAt, token: CancellationToken) =
    interface IReadTx with
        member _.ObservedAt = observedAt

        member _.Authority
            ({
                 AuthorityQuery.Principal = principal
                 CaseId = caseId
             })
            =
            task {
                let! actor = Projections.actor connection principal (Option.toList caseId) token

                match caseId with
                | Some _ -> return actor
                | None ->
                    return!
                        Projections.assigned
                            connection
                            principal
                            (Authority.readRoles principal)
                            actor
                            token
            }

        member _.FindCase id =
            Projections.caseById connection installation id token

        member _.FindEntry id =
            StoredEntries.operation connection installation id token

        member _.CasePage query =
            TransactionPages.cases connection installation query token

        member _.HistoryPage query =
            TransactionPages.history connection installation query token

        member _.ActorPage
            ({
                 ActorPageQuery.AfterActorId = after
                 Limit = limit
             })
            =
            TransactionPages.actors connection after limit token

        member _.GrantPage
            ({
                 GrantPageQuery.ActorId = id
                 AfterGrant = after
                 Limit = limit
             })
            =
            TransactionPages.grants connection id after limit token

        member _.FindActor id =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT actor_id,principal_kind,issuer,subject,enabled FROM claimcore.actors WHERE actor_id=$1"
                        [| box (ActorId.value id) |]
                        token

                return rows |> List.tryExactlyOne |> Option.map Projections.actorRow
            }

        member _.EnabledHumanOwners() =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT count(*)::int FROM claimcore.actors a JOIN claimcore.grants g USING(actor_id) WHERE a.enabled AND a.principal_kind='HUMAN' AND g.role=$1 AND g.case_id IS NULL"
                        [| box (DurableNames.role Role.Owner) |]
                        token

                return DatabaseSession.value<int> 0 rows.Head
            }

        member _.ActorCount() =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT count(*)::int FROM claimcore.actors"
                        [||]
                        token

                return DatabaseSession.value<int> 0 rows.Head
            }

/// Construction is private and occurs only after lock_ledger_head returns in IStore.Write.
type private WriteTx(connection, installation, head, observedAt, nonce, token) =
    inherit ReadTx(connection, installation, observedAt, token)

    interface IWriteTx with
        member _.Head = head
        member _.Nonce = nonce

        member _.IsFenced
            ({
                 FenceKey.ActorId = actor
                 OperationId = operation
             })
            =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT EXISTS (SELECT 1 FROM claimcore.operation_fences WHERE actor_id=$1 AND operation_id=$2)"
                        [| box (ActorId.value actor); box (OperationId.value operation) |]
                        token

                return DatabaseSession.value<bool> 0 rows.Head
            }

        member _.Append entry =
            task { let! _ = Persistence.entry connection entry token in return () }

        member _.ProjectCase projection =
            Persistence.case connection projection token

        member _.ProjectAuthority projection =
            Persistence.authority connection projection token

        member _.Fence
            ({
                 FenceKey.ActorId = actor
                 OperationId = operation
             })
            =
            task {
                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.operation_fences(actor_id,operation_id,fenced_at) VALUES ($1,$2,$3) ON CONFLICT (actor_id,operation_id) DO NOTHING"
                        [|
                            box (ActorId.value actor)
                            box (OperationId.value operation)
                            box observedAt
                        |]
                        token

                return ()
            }

type internal LedgerStore(source: NpgsqlDataSource, installationId, settings) =
    interface IStore with
        member _.Read
            ({
                 ReadOperation.Work = work
                 CancellationToken = token
             })
            =
            Transactions.read source token (fun connection ->
                task {
                    let! rows =
                        DatabaseSession.rows connection "SELECT clock_timestamp()" [||] token

                    let observedAt = DatabaseSession.instant 0 rows.Head
                    return! work (ReadTx(connection, installationId, observedAt, token) :> IReadTx)
                })

        member _.Write
            ({
                 WriteOperation.Work = work
                 CancellationToken = token
             })
            =
            Transactions.write source settings token (fun connection ->
                task {
                    let! head, observedAt, nonce = Transactions.head connection token

                    let! verdict =
                        work (
                            WriteTx(connection, installationId, head, observedAt, nonce, token)
                            :> IWriteTx
                        )

                    let commit =
                        match verdict with
                        | Verdict.Proceed _ -> true
                        | Verdict.Replay _
                        | Verdict.Refuse _
                        | Verdict.Fail _ -> false

                    return verdict, commit
                })
