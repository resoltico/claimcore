namespace ClaimCore.Application.Storage

open ClaimCore.Application.Verification

open ClaimCore.Application.Reads

open ClaimCore.Application.Execution

open ClaimCore.Application

open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// The operator isolates ALL client traffic before calling this owner-only boundary.
/// Maintenance is checked here; no database query can prove external traffic isolation.
module internal RestoreAdmission =
    let admit
        (source: NpgsqlDataSource)
        masterKey
        (trusted: InstallationCheckpoint)
        (handle: MaintenanceHandle)
        (token: CancellationToken)
        =
        task {
            let! prepared =
                RegisterMaintenanceStore.prepare
                    source
                    masterKey
                    handle
                    token
                    (fun connection publication cipher ->
                        task {
                            let installation = publication.Installation

                            match
                                CheckpointEvidence.trustedInstallation
                                    installation.InstallationId
                                    trusted
                            with
                            | Error finding -> return Error finding
                            | Ok() ->
                                let! history =
                                    Verification.authoritative
                                        connection
                                        installation
                                        cipher
                                        (Some trusted.Position)
                                        token

                                return history |> Result.map (fun _ -> publication)
                        })

            match prepared with
            | Error finding -> return Error finding
            | Ok publication ->
                return!
                    RegisterMaintenanceStore.publish
                        source
                        masterKey
                        publication
                        token
                        (fun connection _ ->
                            task {
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

                                let! checkpoint =
                                    CheckpointEvidence.read
                                        connection
                                        publication.Installation.InstallationId
                                        token

                                return
                                    Ok
                                        {
                                            Checkpoint = checkpoint
                                            InvalidatedSessions = int64 removed
                                        }
                            })
        }
