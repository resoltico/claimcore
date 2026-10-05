namespace ClaimCore.Witness

open System
open System.Threading
open Npgsql

/// A claimant-bearing read holds the definer-acquired row lock through its core response.
module internal WitnessStoreReadLease =
    let acquire
        (source: NpgsqlDataSource)
        identity
        (capability: byte array)
        expectedGeneration
        (ct: CancellationToken)
        =
        task {
            let! connection = source.OpenConnectionAsync(ct)

            try
                do! WitnessDatabaseAdmission.checkAsync identity connection ct
                let! transaction = connection.BeginTransactionAsync(ct)

                try
                    let! current =
                        WitnessStoreRead.lockWriterAdmission
                            identity
                            capability
                            connection
                            transaction
                            ct

                    if current <> expectedGeneration then
                        invalidOp "Witness writer generation changed."

                    return
                        { new IDisposable with
                            member _.Dispose() =
                                try
                                    transaction.Dispose()
                                finally
                                    connection.Dispose()
                        }
                with error ->
                    transaction.Dispose()
                    return raise error
            with error ->
                connection.Dispose()
                return raise error
        }
