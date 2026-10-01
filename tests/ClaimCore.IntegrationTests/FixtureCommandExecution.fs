module internal ClaimCore.IntegrationTests.FixtureCommandExecution

open System.Threading
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.RecordFormat

let private failure operationId =
    function
    | RecoveryStoreFailure.TechnicalMutationUnknown -> CoreFailure.CommitOutcomeUnknown operationId
    | RecoveryStoreFailure.IdempotencyConflict -> CoreFailure.IdempotencyConflict
    | RecoveryStoreFailure.ResourceUnavailable -> CoreFailure.ResourceUnavailable
    | RecoveryStoreFailure.StoreCorrupt -> CoreFailure.StoreCorrupt
    | RecoveryStoreFailure.SchemaMismatch -> CoreFailure.SchemaMismatch
    | _ -> CoreFailure.StoreUnavailable

let private material operation (context: ActorCallContext) =
    {
        OperationId = (Operation.request operation).OperationId
        CaseId =
            context.CaseId
            |> Option.defaultWith (fun () -> invalidOp "Test case identity is absent.")
        PreparerActorId = context.Binding.ActorId
        ImporterActorId = None
        PreparerGrantRevision = context.Binding.GrantRevision
        CanonicalRequestFormat = RecordVersions.CanonicalCommandFormat
        RequestSha256 = Operation.fingerprint operation
        CanonicalRequest = Operation.canonicalRequest operation
        PreparingApplicationVersion = BuildIdentity.current.Version
        PreparingContractFingerprint =
            SemanticContract.fingerprint SemanticContract.current
            |> SemanticCoreFingerprint.value
        PreparingContractKind = PreparingContractKind.SemanticCoreV1
    }

let private admitted (port: IRecoveryStore) operation capture decide =
    task {
        let operationId = (Operation.request operation).OperationId

        match! port.Start(operationId, CancellationToken.None) with
        | Error error -> return Error(failure operationId error)
        | Ok(RecoveryStart.ObservedAccepted receipt) -> return Ok receipt
        | Ok(RecoveryStart.Dismissed _) -> return Error CoreFailure.ResourceUnavailable
        | Ok(RecoveryStart.Started(attempt, _))
        | Ok(RecoveryStart.AlreadyStarted(attempt, _)) ->
            match!
                port.ExecuteAdmitted(operation, attempt, capture, decide, CancellationToken.None)
            with
            | Ok(AdmittedExecution.ObservedAccepted receipt)
            | Ok(AdmittedExecution.Accepted receipt) -> return Ok receipt
            | Ok(AdmittedExecution.Rejected(error, _)) -> return Error(CoreFailure.Domain error)
            | Ok(AdmittedExecution.CommitOutcomeUnknown _) ->
                return Error(CoreFailure.CommitOutcomeUnknown operationId)
            | Ok(AdmittedExecution.FailedBeforeCommit(error, _)) -> return Error error
            | Ok(AdmittedExecution.RevokedBeforeExecution _) ->
                return Error CoreFailure.ResourceUnavailable
            | Error error -> return Error(failure operationId error)
    }

/// Exercise the real preparation/attempt/command protocol, including exact witnessed replay.
let execute source witness context operation capture decide =
    task {
        use claims =
            new PostgresStore(
                source,
                witness,
                context,
                CaseListCursorTestSupport.protection,
                CaseListCursorTestSupport.clock
            )

        let operationId = (Operation.request operation).OperationId

        match! (claims :> IClaimStore).Accepted(operationId, Operation.fingerprint operation) with
        | Error error -> return Error error
        | Ok(Some receipt) -> return Ok receipt
        | Ok None ->
            let port =
                PostgresRecoveryStore(source, PreparationLimits.defaults, witness, context)
                :> IRecoveryStore

            match! port.Retain(material operation context, CancellationToken.None) with
            | Error error -> return Error(failure operationId error)
            | Ok(RecoveryRetain.ObservedAccepted receipt) -> return Ok receipt
            | Ok(RecoveryRetain.Revoked _) -> return Error CoreFailure.ResourceUnavailable
            | Ok _ -> return! admitted port operation capture decide
    }

let executeRequest source witness context (clock: IBusinessTime) request =
    match Operation.prepare request with
    | Error error -> System.Threading.Tasks.Task.FromResult(Error(CoreFailure.Domain error))
    | Ok operation ->
        execute source witness context operation clock.Capture (fun date current ->
            Claim.decide date request current)
