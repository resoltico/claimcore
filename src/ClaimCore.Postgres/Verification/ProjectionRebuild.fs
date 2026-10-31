namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Owner-only maintenance. The ledger head lock excludes application writers throughout the scan.
module internal ProjectionRebuild =
    let private snapshot id (history: CaseHistory) =
        match history.Snapshot, history.State, history.Reference with
        | Some snapshot, _, _ -> snapshot
        | None, CaseState.Erased, Some reference ->
            {
                CaseId = id
                Reference = reference
                Revision = history.Revision
                State = CaseState.Erased
            }
        | _ -> raise (RecordCorruptException "Authoritative case reconstruction is incomplete.")

    let private replaceCases connection (state: LedgerVerification.Scan) token =
        task {
            for KeyValue(id, history) in state.Cases do
                let snapshot = snapshot id history

                match snapshot.State with
                | CaseState.Erased ->
                    let! _ =
                        DatabaseSession.execute
                            connection
                            "INSERT INTO claimcore.cases(case_id,case_reference,revision,state,changed_at) VALUES ($1,$2,$3,'ERASED',$4)"
                            [|
                                box (CaseId.value id)
                                box (CaseReference.format snapshot.Reference)
                                box (Revision.value snapshot.Revision)
                                box history.ChangedAt
                            |]
                            token

                    ()
                | _ ->
                    do!
                        Persistence.case
                            connection
                            (CaseProjection.Insert(
                                id,
                                snapshot,
                                CaseIndex.create history.ChangedAt snapshot
                            ))
                            token
        }

    let private replaceAuthority connection (state: LedgerVerification.Scan) token =
        task {
            for KeyValue(id, actor) in state.Actors do
                do!
                    Persistence.authority
                        connection
                        (AuthorityProjection.Register(id, actor.Principal))
                        token

                do!
                    Persistence.authority
                        connection
                        (AuthorityProjection.SetEnabled(id, actor.Enabled))
                        token

                for grant in actor.Grants do
                    let caseId =
                        match grant.Scope with
                        | Scope.Installation -> None
                        | Scope.Case id -> Some id

                    do!
                        Persistence.authority
                            connection
                            (AuthorityProjection.AddGrant(id, grant.Role, caseId))
                            token

            for KeyValue((actor, operation), instant) in state.Fences do
                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.operation_fences(actor_id,operation_id,fenced_at) VALUES ($1,$2,$3)"
                        [|
                            box (ActorId.value actor)
                            box (OperationId.value operation)
                            box instant
                        |]
                        token

                ()
        }

    let rebuild (source: NpgsqlDataSource) expected (token: CancellationToken) =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    ServerSupport.require connection.PostgreSqlVersion
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! _ = Transactions.head connection token
                    let! installation, _ = Baseline.require connection token

                    let! scanned =
                        Verification.authoritative connection installation (Some expected) token

                    match scanned with
                    | Error finding -> return Error finding
                    | Ok state ->
                        let! before = Verification.projections connection installation state token
                        // DELETE preserves old MVCC snapshots; TRUNCATE and table swapping do not.
                        for table in [ "grants"; "actors"; "cases"; "operation_fences" ] do
                            let! _ =
                                DatabaseSession.execute
                                    connection
                                    ("DELETE FROM claimcore." + table)
                                    [||]
                                    token

                            ()

                        do! replaceCases connection state token
                        do! replaceAuthority connection state token

                        let! validated =
                            Verification.projections connection installation state token

                        match validated with
                        | Error finding -> return Error finding
                        | Ok() ->
                            do! DatabaseSession.commit transaction token

                            return
                                Ok
                                    {
                                        ProjectionRebuildReport.Position = state.Head
                                        PreviousDivergence =
                                            match before with
                                            | Error finding -> Some finding
                                            | Ok() -> None
                                    }
                })

    let invalidateRestoredSessions (source: NpgsqlDataSource) (token: CancellationToken) =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    ServerSupport.require connection.PostgreSqlVersion
                    let! _ = Baseline.require connection token
                    let! _ = Transactions.head connection token

                    let! deleted =
                        DatabaseSession.execute
                            connection
                            "DELETE FROM claimcore.sessions"
                            [||]
                            token

                    do! DatabaseSession.commit transaction token
                    return int64 deleted
                })
