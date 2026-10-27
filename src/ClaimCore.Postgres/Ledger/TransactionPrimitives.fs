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

    let cases connection visibility actor roles forbidden after limit token =
        task {
            let! rows =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {Projections.caseColumns} FROM claimcore.cases c WHERE ($1::text IS NULL OR upper(case_reference COLLATE \"C\") > $1 COLLATE \"C\") AND ($2 OR c.case_id IN (SELECT g.case_id FROM claimcore.grants g WHERE g.actor_id=$3 AND g.role=ANY($4::text[]) AND g.case_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM claimcore.grants bad WHERE bad.actor_id=g.actor_id AND bad.case_id=g.case_id AND bad.role=ANY($5::text[])))) ORDER BY upper(case_reference COLLATE \"C\") LIMIT $6")
                    [|
                        DatabaseSession.parameter after
                        box (visibility = CaseVisibility.Every)
                        box actor
                        box (roles |> List.map RoleTokens.token |> List.toArray)
                        box (forbidden |> List.map RoleTokens.token |> List.toArray)
                        box (limit + 1)
                    |]
                    token

            return
                rows
                |> List.map Projections.caseRow
                |> page limit (fun record -> CaseReference.identity record.Snapshot.Reference)
        }

    let history connection installation id after limit token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {StoredEntries.columns} FROM claimcore.ledger WHERE subject_kind='CASE' AND subject_id=$1 AND case_revision>$2 ORDER BY case_revision LIMIT $3")
                    [| box id; box after; box (limit + 1) |]
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
                    [| DatabaseSession.parameter after; box (limit + 1) |]
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
                        box target
                        DatabaseSession.parameter (
                            after |> Option.map (fun value -> RoleTokens.token value.Role)
                        )
                        DatabaseSession.parameter (after |> Option.bind _.CaseId)
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

        member _.Authority(principal, caseId) =
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
            Projections.caseById connection id token

        member _.FindEntry id =
            StoredEntries.operation connection installation id token

        member _.CasePage(visibility, actor, roles, forbidden, after, limit) =
            TransactionPages.cases connection visibility actor roles forbidden after limit token

        member _.HistoryPage(id, after, limit) =
            TransactionPages.history connection installation id after limit token

        member _.ActorPage(after, limit) =
            TransactionPages.actors connection after limit token

        member _.GrantPage(id, after, limit) =
            TransactionPages.grants connection id after limit token

        member _.FindActor id =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT actor_id,principal_kind,issuer,subject,enabled FROM claimcore.actors WHERE actor_id=$1"
                        [| box id |]
                        token

                return rows |> List.tryExactlyOne |> Option.map Projections.actorRow
            }

        member _.EnabledHumanOwners() =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT count(*)::int FROM claimcore.actors a JOIN claimcore.grants g USING(actor_id) WHERE a.enabled AND a.principal_kind='HUMAN' AND g.role=$1 AND g.case_id IS NULL"
                        [| box (RoleTokens.token Role.Owner) |]
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

        member _.IsFenced(actor, operation) =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT EXISTS (SELECT 1 FROM claimcore.operation_fences WHERE actor_id=$1 AND operation_id=$2)"
                        [| box actor; box operation |]
                        token

                return DatabaseSession.value<bool> 0 rows.Head
            }

        member _.Append entry =
            task { let! _ = Persistence.entry connection entry token in return () }

        member _.ProjectCase projection =
            Persistence.case connection projection token

        member _.ProjectAuthority projection =
            Persistence.authority connection projection token

        member _.Fence(actor, operation) =
            task {
                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.operation_fences(actor_id,operation_id,fenced_at) VALUES ($1,$2,$3) ON CONFLICT (actor_id,operation_id) DO NOTHING"
                        [| box actor; box operation; box observedAt |]
                        token

                return ()
            }

type internal LedgerStore(source: NpgsqlDataSource, installationId, settings) =
    interface IStore with
        member _.Read(work, token) =
            Transactions.read source token (fun connection ->
                task {
                    let! rows =
                        DatabaseSession.rows connection "SELECT clock_timestamp()" [||] token

                    let observedAt = DatabaseSession.instant 0 rows.Head
                    return! work (ReadTx(connection, installationId, observedAt, token) :> IReadTx)
                })

        member _.Write(work, token) =
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
