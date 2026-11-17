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

    let change (source: NpgsqlDataSource) change (token: CancellationToken) =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! position, observed = Transactions.head connection token
                    let! _ = Baseline.require connection token
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
