namespace ClaimCore.Postgres

open ClaimCore.Application
open WitnessProtocolReconciliation

/// Caller holds current actor authority and the operation lock. Content conflict precedes
/// receipt decoding; a definite recovery result additionally needs exact independent settlement.
module internal RecoveryAcceptedObservation =
    let read connection transaction (witness: WitnessProtocol) operationId digest ct =
        task {
            match! StoreData.readAcceptedUnderLock connection transaction operationId digest with
            | Error CoreFailure.IdempotencyConflict ->
                return Error RecoveryStoreFailure.IdempotencyConflict
            | Error _ -> return Error RecoveryStoreFailure.StoreCorrupt
            | Ok None -> return Ok None
            | Ok(Some receipt) ->
                do! witness.ReconcileAccepted(connection, transaction, operationId, ct)
                return Ok(Some receipt)
        }
