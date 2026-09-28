namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

/// The witnessed primary commit and accepted-row projection shared by normal submission and
/// recovery resolution. The caller has already locked and authorized the exact actor/case.
module internal StoreTransactionPersistence =
    let private commit
        (transaction: NpgsqlTransaction)
        (commitStarted: bool ref)
        (witness: WitnessProtocol)
        operationId
        intent
        receipt
        =
        task {
            commitStarted.Value <- true
            do! transaction.CommitAsync()
            witness.SettleAccepted(operationId, intent) |> ignore
            return Ok receipt
        }

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

    let persistDecision
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operation
        context
        current
        caseId
        attribution
        claim
        (witness: WitnessProtocol)
        commitStarted
        =
        task {
            let request = Operation.request operation

            let intent =
                WitnessAcceptedProtocol.beginAccepted
                    witness
                    operation
                    context
                    caseId
                    attribution
                    claim

            let! receipt =
                task {
                    try
                        return!
                            persistUnderCaseLock
                                connection
                                transaction
                                operation
                                context
                                current
                                caseId
                                attribution
                                claim
                                intent.Ticket
                    with _ ->
                        return raise WitnessPending
                }

            return! commit transaction commitStarted witness request.OperationId intent receipt
        }
