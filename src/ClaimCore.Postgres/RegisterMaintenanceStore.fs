namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application.Contracts

/// Rebuild is an owner operation, but it still requires explicit register-write maintenance.
type MaintenanceRequiredException() =
    inherit InvalidOperationException("Enter register maintenance before rebuilding projections.")

module internal RegisterMaintenanceStore =
    let read connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT active,generation FROM claimcore.register_maintenance"
                    [||]
                    token

            match rows with
            | [ row ] ->
                return
                    RegisterMaintenance.create
                        (DatabaseSession.value 0 row)
                        (DatabaseSession.value 1 row)
                    |> Result.defaultWith (fun _ ->
                        raise (RecordCorruptException "Invalid register maintenance state."))
            | _ -> return raise (RecordCorruptException "Register maintenance state is absent.")
        }

    let requireActive connection token =
        task {
            let! current = read connection token

            if not current.Active then
                return raise (MaintenanceRequiredException())
        }

    let change (source: NpgsqlDataSource) change (token: CancellationToken) =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! position, observed, _ = Transactions.head connection token
                    let! _ = Baseline.require connection token
                    let! current = read connection token

                    let proposed =
                        match change with
                        | MaintenanceChange.Enter -> RegisterMaintenance.enter current
                        | MaintenanceChange.Resume generation ->
                            RegisterMaintenance.resume generation current

                    let observation state changed =
                        {
                            State = state
                            ObservedAt = observed
                            Position = position
                            Changed = changed
                        }

                    match proposed with
                    | Error actual -> return Error(observation actual false)
                    | Ok after ->
                        let changed = after <> current

                        if changed then
                            let! count =
                                DatabaseSession.execute
                                    connection
                                    "UPDATE claimcore.register_maintenance SET active=$1,generation=$2 WHERE singleton"
                                    [| box after.Active; box after.Generation |]
                                    token

                            if count <> 1 then
                                raise (
                                    RecordCorruptException "Register maintenance state is absent."
                                )

                        do! DatabaseSession.commit transaction token
                        return Ok(observation after changed)
                })
