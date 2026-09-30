namespace ClaimCore.Witness

open System
open Npgsql

/// A claimant-bearing read holds the definer-acquired row lock through its core response.
module internal WitnessStoreReadLease =
    let acquire (source: NpgsqlDataSource) identity (capability: byte array) expectedGeneration =
        let connection = source.OpenConnection()

        try
            WitnessStoreRead.checkAdmission identity connection
            let transaction = connection.BeginTransaction()

            try
                let current =
                    WitnessStoreRead.lockWriterAdmission identity capability connection transaction

                if current <> expectedGeneration then
                    invalidOp "Witness writer generation changed."

                { new IDisposable with
                    member _.Dispose() =
                        transaction.Dispose()
                        connection.Dispose()
                }
            with _ ->
                transaction.Dispose()
                reraise ()
        with _ ->
            connection.Dispose()
            reraise ()
