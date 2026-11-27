namespace ClaimCore.Application.Storage

open ClaimCore.Application.Reads

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open System.Threading
open Npgsql
open ClaimCore.Application.Contracts

module internal RegisterMaintenanceStore =
    let read connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT generation,pause_id FROM claimcore.register_maintenance"
                    [||]
                    token

            match rows with
            | [ row ] ->
                return
                    RegisterMaintenance.create
                        (DatabaseSession.value<int64> 0 row)
                        (DatabaseSession.optional<Guid> 1 row)
            | _ -> return raise (RecordCorruptException "Register maintenance state is absent.")
        }

    let requireActive expected connection token =
        task {
            let! current = read connection token

            if not current.Active then
                return raise (MaintenanceRequiredException())
            elif current.Handle <> Some expected then
                return raise (OperatorFaultException OperatorFault.MaintenanceConflict)
        }

    type Publication =
        {
            Installation: Installation
            Checkpoint: ClaimCore.Domain.InstallationCheckpoint
            Maintenance: MaintenanceHandle
            Generation: int64
        }

    let publicationGeneration connection token =
        task {
            let! generations =
                DatabaseSession.rows
                    connection
                    "SELECT publication_generation FROM claimcore.register_maintenance WHERE singleton"
                    [||]
                    token

            match generations with
            | [ row ] -> return DatabaseSession.value<int64> 0 row
            | _ -> return raise (RecordCorruptException "Register maintenance state is absent.")
        }

    let private evidence (installation: Installation) handle connection token =
        task {
            do! requireActive handle connection token

            let! checkpoint =
                CheckpointEvidence.read connection installation.InstallationId token

            let! generation = publicationGeneration connection token

            return
                {
                    Installation = installation
                    Checkpoint = checkpoint
                    Maintenance = handle
                    Generation = generation
                }
        }

    let prepare source masterKey handle token work =
        DatabaseSession.read source token (fun connection ->
            task {
                do! OwnerAdmission.require connection token
                let! _ = Transactions.configure connection Transactions.timeouts token
                let! installation = Baseline.require connection token
                let! publication = evidence installation handle connection token

                use! cipher =
                    Baseline.loadCipher connection installation.InstallationId masterKey token

                return! work connection publication cipher
            })

    let private withFence source masterKey (prepared: Publication) token stage work =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    do! OwnerAdmission.require connection token
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    do! stage connection
                    let! _ = Transactions.head connection token
                    let! installation = Baseline.require connection token
                    let! current = evidence installation prepared.Maintenance connection token

                    if current <> prepared then
                        raise (OperatorFaultException OperatorFault.StaleRecoveryPreparation)

                    use! cipher =
                        Baseline.loadCipher connection installation.InstallationId masterKey token

                    return! work connection transaction cipher
                })

    let private withoutStaging _ = task { return () }

    let publishStaged source masterKey prepared token stage work =
        withFence source masterKey prepared token stage (fun connection transaction cipher ->
            task {
                let! _ = WitnessPrimary.configureDisclosure connection token
                let! result = work connection cipher

                match result with
                | Error _ -> return result
                | Ok _ ->
                    do! Baseline.advanceRecoveryPublication connection token
                    do! DatabaseSession.commit transaction token
                    return result
            })

    let publish source masterKey prepared token work =
        publishStaged source masterKey prepared token withoutStaging work

    /// Re-read evidence under the maintenance and head protection used for publication.
    let confirmWith source masterKey prepared token observe =
        withFence source masterKey prepared token withoutStaging (fun connection _ cipher ->
            observe connection cipher)

    let reserveRestoreTarget source masterKey prepared generation token validate =
        withFence source masterKey prepared token withoutStaging (fun connection transaction _ ->
            task {
                do! validate connection

                let! existing =
                    DatabaseSession.rows
                        connection
                        "SELECT primary_id FROM claimcore.restore_targets WHERE generation=$1"
                        [| box generation |]
                        token

                match existing with
                | [ row ] -> return DatabaseSession.value<Guid> 0 row
                | _ ->
                    let! _ = WitnessPrimary.configureDisclosure connection token

                    let! reserved =
                        DatabaseSession.rows
                            connection
                            "INSERT INTO claimcore.restore_targets(generation) VALUES ($1) RETURNING primary_id"
                            [| box generation |]
                            token

                    do! DatabaseSession.commit transaction token
                    return DatabaseSession.value<Guid> 0 (List.exactlyOne reserved)
            })

    /// A successful fenced observation has no durable write, commit or generation advance.
    let confirm source masterKey prepared token value =
        confirmWith source masterKey prepared token (fun _ _ ->
            System.Threading.Tasks.Task.FromResult value)

    /// Apply one transition inside the caller's transaction while it owns the common head lock.
    let changeWithinHeadLock connection change token =
        task {
            let! current = read connection token

            let after =
                MaintenanceChange.apply current change
                |> Option.defaultWith (fun () ->
                    raise (OperatorFaultException OperatorFault.MaintenanceConflict))

            let changed = after <> current

            if changed then
                let! count =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.register_maintenance SET generation=$1,pause_id=$2 WHERE singleton"
                        [| box after.Generation; DatabaseSession.parameter after.PauseId |]
                        token

                if count <> 1 then
                    raise (RecordCorruptException "Register maintenance state is absent.")

            return after, changed
        }

    let reopenPending connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT reopen_pending FROM claimcore.recovery_generation WHERE singleton"
                    [||]
                    token

            return DatabaseSession.value<bool> 0 (List.exactlyOne rows)
        }

    let private changeChecked
        (source: NpgsqlDataSource)
        masterKey
        change
        reopening
        (token: CancellationToken)
        =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    do! OwnerAdmission.require connection token
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! position, observed = Transactions.head connection token
                    let! installation = Baseline.require connection token

                    use! _cipher =
                        Baseline.loadCipher connection installation.InstallationId masterKey token

                    match change with
                    | MaintenanceChange.Resume _ ->
                        let! pending = reopenPending connection token

                        if pending then
                            match reopening with
                            | Some(generation, target, date) when
                                (InstallationCheck.calendar installation.BusinessTimeZone).DateAt
                                    observed
                                    =
                                    date
                                ->
                                let! cleared =
                                    DatabaseSession.execute
                                        connection
                                        """UPDATE claimcore.recovery_generation SET reopen_pending=false
                                           WHERE generation=$1 AND primary_id=$2 AND reopen_pending"""
                                        [| box generation; box target |]
                                        token

                                if cleared <> 1 then
                                    raise (
                                        OperatorFaultException
                                            OperatorFault.StaleRecoveryPreparation
                                    )
                            | _ ->
                                raise (
                                    OperatorFaultException OperatorFault.StaleRecoveryPreparation
                                )
                    | _ -> ()

                    let! after, changed = changeWithinHeadLock connection change token

                    do! DatabaseSession.commit transaction token

                    return
                        {
                            State = after
                            ObservedAt = observed
                            Position = position
                            Changed = changed
                        }
                })


    let change source masterKey requested token =
        changeChecked source masterKey requested None token

    let changeForReopen source masterKey handle evidence token =
        changeChecked source masterKey (MaintenanceChange.Resume handle) (Some evidence) token
