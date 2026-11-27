namespace ClaimCore.Application.Storage

open System.Data
open ClaimCore.RecordFormat
open Npgsql

module internal EncryptionBudget =
    let reserve (source: NpgsqlDataSource) generation count token =
        task {
            try
                return!
                    DatabaseSession.transaction
                        source
                        IsolationLevel.ReadCommitted
                        token
                        (fun connection tx ->
                            task {
                                let! _ = WitnessPrimary.configureDisclosure connection token

                                let! rows =
                                    DatabaseSession.rows
                                        connection
                                        "SELECT claimcore.reserve_encryptions($1,$2)"
                                        [| box generation; box count |]
                                        token

                                do! DatabaseSession.commit tx token
                                return Ok(DatabaseSession.value<bool> 0 (List.exactlyOne rows))
                            })
            with error ->
                // A lost acknowledgement burns the reservation and cannot authorize encryption.
                return Error(Transactions.failure false error)
        }


    let reserveActive source count token =
        task {
            let! active, _ =
                DatabaseSession.read source token (fun connection ->
                    Baseline.keyGenerations connection token)

            match! reserve source active count token with
            | Ok true -> return active
            | _ ->
                return
                    raise (
                        OperatorFaultException
                            ClaimCore.Application.Contracts.OperatorFault.DataKeyInvalid
                    )
        }
