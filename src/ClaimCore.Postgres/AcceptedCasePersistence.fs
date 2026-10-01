namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

/// Persists one decided current projection and its accepted receipt inside the sole command
/// writer. The caller has already locked and authorised the exact actor, operation and case.
module internal AcceptedCasePersistence =
    let persistUnderCaseLock
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operation
        context
        current
        caseId
        (attribution: ExecutionAttribution)
        claim
        ticket
        =
        task {
            let request = Operation.request operation
            let snapshot = Claim.view claim

            let previous =
                current
                |> Option.map (fun value -> (Claim.view value).Version)
                |> Option.defaultValue 0L

            if
                snapshot.Fields.CaseReference <> request.CaseReference
                || snapshot.Version <> previous + 1L
            then
                raise (
                    InvalidDataException(
                        "The domain result did not satisfy the persistence contract."
                    )
                )

            return!
                StoreData.persist
                    connection
                    transaction
                    operation
                    context
                    caseId
                    attribution
                    claim
                    ticket
        }
