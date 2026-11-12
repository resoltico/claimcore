namespace ClaimCore.Postgres

open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// The operator isolates ALL client traffic before calling this owner-only boundary.
/// Maintenance is checked here; no database query can prove external traffic isolation.
module internal RestoreAdmission =
    let admit
        (source: NpgsqlDataSource)
        (trusted: InstallationCheckpoint)
        (token: CancellationToken)
        =
        DatabaseSession.transaction
            source
            System.Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    ServerSupport.require connection.PostgreSqlVersion
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! _ = Transactions.head connection token
                    do! RegisterMaintenanceStore.requireActive connection token
                    let! installation, _ = Baseline.require connection token

                    match CheckpointEvidence.installation installation trusted with
                    | Error finding -> return Error finding
                    | Ok() ->
                        let! history =
                            Verification.authoritative
                                connection
                                installation
                                (Some trusted.Position)
                                token

                        match history with
                        | Error finding -> return Error finding
                        | Ok _ ->
                            match trusted.ExpiredBefore with
                            | Some cutoff ->
                                let! _ = ExpiryFrontierStorage.advance connection cutoff token
                                ()
                            | None -> ()

                            let! removed =
                                DatabaseSession.execute
                                    connection
                                    "DELETE FROM claimcore.sessions"
                                    [||]
                                    token

                            let! checkpoint = CheckpointEvidence.read connection installation token
                            do! DatabaseSession.commit transaction token

                            return
                                Ok
                                    {
                                        Checkpoint = checkpoint
                                        InvalidatedSessions = int64 removed
                                    }
                })
