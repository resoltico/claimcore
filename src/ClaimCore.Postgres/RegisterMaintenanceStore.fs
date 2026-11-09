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
                    "SELECT active FROM claimcore.register_maintenance"
                    [||]
                    token

            match rows with
            | [ row ] -> return RegisterMaintenance.create (DatabaseSession.value<bool> 0 row)
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
                    let! position, observed = Transactions.head connection token
                    let! _ = Baseline.require connection token
                    let! current = read connection token

                    let after = RegisterMaintenance.create (change = MaintenanceChange.On)
                    let changed = after <> current

                    if changed then
                        let! count =
                            DatabaseSession.execute
                                connection
                                "UPDATE claimcore.register_maintenance SET active=$1 WHERE singleton"
                                [| box after.Active |]
                                token

                        if count <> 1 then
                            raise (RecordCorruptException "Register maintenance state is absent.")

                    do! DatabaseSession.commit transaction token

                    return
                        {
                            State = after
                            ObservedAt = observed
                            Position = position
                            Changed = changed
                        }
                })
