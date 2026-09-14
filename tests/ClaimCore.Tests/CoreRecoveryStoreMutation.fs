namespace ClaimCore.Tests

open System
open System.Threading.Tasks
open ClaimCore.Application

module internal RecoveryStoreMutation =
    let private unresolvedAttempt state operationId =
        RecoveryStoreState.attemptsFor state operationId
        |> List.tryFind (fun attempt -> attempt.Settlement.IsNone)

    let private startFreshAttempt state operationId preparation =
        match unresolvedAttempt state operationId with
        | Some attempt -> Ok(RecoveryStart.AlreadyStarted(attempt.AttemptId, preparation))
        | None when RecoveryStoreState.attemptsFor state operationId |> List.length >= 64 ->
            Error RecoveryStoreFailure.CapacityExceeded
        | None ->
            let attemptId = Guid.NewGuid()
            let startedAt = DateTimeOffset.UtcNow

            state.Attempts <-
                Map.add
                    operationId
                    [
                        {
                            AttemptId = attemptId
                            StartedAt = startedAt
                            Settlement = None
                            SettledAt = None
                        }
                    ]
                    state.Attempts

            { preparation with
                Lifecycle = PreparationLifecycle.SubmissionStarted startedAt
            }
            |> RecoveryStoreState.update state
            |> fun started -> RecoveryStart.Started(attemptId, started)
            |> Ok

    let private startRetained state operationId preparation =
        match preparation.Lifecycle with
        | PreparationLifecycle.Dismissed _ -> Ok(RecoveryStart.Dismissed preparation)
        | PreparationLifecycle.Unsubmitted -> startFreshAttempt state operationId preparation
        | PreparationLifecycle.SubmissionStarted _ ->
            match unresolvedAttempt state operationId with
            | Some attempt -> Ok(RecoveryStart.AlreadyStarted(attempt.AttemptId, preparation))
            | None -> Error RecoveryStoreFailure.StoreCorrupt

    let private startResult state settings operationId =
        match settings.StartFailure with
        | Some failure -> Error failure
        | None ->
            match Map.tryFind operationId state.Accepted, Map.tryFind operationId state.Values with
            | Some receipt, _ -> Ok(RecoveryStart.ObservedAccepted receipt)
            | None, Some preparation -> startRetained state operationId preparation
            | None, None -> Error RecoveryStoreFailure.NotFound

    let start (state: RecoveryStoreStateData) (settings: RecoveryStoreSettings) operationId =
        let result =
            lock state.Gate (fun () ->
                state.StartCalls <- state.StartCalls + 1
                startResult state settings operationId)

        settings.OnStart |> Option.iter (fun callback -> callback ())
        Task.FromResult result

    let settle (state: RecoveryStoreStateData) (settings: RecoveryStoreSettings) attemptId outcome =
        lock state.Gate (fun () -> state.SettlementCalls <- state.SettlementCalls + 1)

        if settings.SettleThrows then
            Task.FromException<Result<unit, RecoveryStoreFailure>>(InvalidOperationException())
        else
            let settlement =
                match outcome with
                | RecoverySettlement.Accepted -> "ACCEPTED"
                | RecoverySettlement.Rejected -> "REJECTED"
                | RecoverySettlement.FailedBeforeCommit -> "FAILED_BEFORE_COMMIT"
                | RecoverySettlement.RevokedBeforeExecution -> "REVOKED_BEFORE_EXECUTION"

            lock state.Gate (fun () ->
                state.Attempts
                |> Map.toSeq
                |> Seq.tryFind (fun (_, attempts) ->
                    attempts |> List.exists (fun attempt -> attempt.AttemptId = attemptId))
                |> Option.iter (fun (operationId, _) ->
                    RecoveryStoreState.recordSettlement state operationId attemptId settlement))

            Task.FromResult(
                settings.SettleFailure |> Option.map Error |> Option.defaultValue (Ok())
            )

    let private settleConfirmed state operationId attemptId outcome =
        RecoveryStoreState.recordSettlement state operationId attemptId outcome
        state.SettlementCalls <- state.SettlementCalls + 1

    let private accepted
        (state: RecoveryStoreStateData)
        attemptId
        (receipt: Receipt)
        : Result<AdmittedExecution, RecoveryStoreFailure> =
        state.Accepted <- Map.add receipt.OperationId receipt state.Accepted
        settleConfirmed state receipt.OperationId attemptId "ACCEPTED"
        Ok(AdmittedExecution.Accepted receipt)

    let private rejected
        (state: RecoveryStoreStateData)
        operationId
        attemptId
        rejection
        : Result<AdmittedExecution, RecoveryStoreFailure> =
        settleConfirmed state operationId attemptId "REJECTED"
        Ok(AdmittedExecution.Rejected(rejection, SettlementConfirmation.Confirmed))

    let private failedBeforeCommit
        (state: RecoveryStoreStateData)
        operationId
        attemptId
        failure
        : Result<AdmittedExecution, RecoveryStoreFailure> =
        settleConfirmed state operationId attemptId "FAILED_BEFORE_COMMIT"
        Ok(AdmittedExecution.FailedBeforeCommit(failure, SettlementConfirmation.Confirmed))

    let private decideAndPersist state operation attemptId today decide =
        let request = Operation.request operation

        match state.ClaimStore with
        | Some store ->
            match
                store.Transact(operation, fun current -> decide (today ()) current)
                |> fun result -> result.GetAwaiter().GetResult()
            with
            | Ok receipt -> accepted state attemptId receipt
            | Error(CoreFailure.Domain rejection) ->
                rejected state request.OperationId attemptId rejection
            | Error(CoreFailure.CommitOutcomeUnknown _) ->
                Ok(AdmittedExecution.CommitOutcomeUnknown request.OperationId)
            | Error failure -> failedBeforeCommit state request.OperationId attemptId failure
        | None ->
            match decide (today ()) None with
            | Ok claim ->
                RecoveryStoreState.receipt state request.OperationId request.Command claim
                |> accepted state attemptId
            | Error rejection -> rejected state request.OperationId attemptId rejection

    let private configuredExecution state settings operation attemptId today decide =
        let operationId = (Operation.request operation).OperationId

        match settings.SettleThrows, settings.SettleFailure with
        | true, _ -> raise (InvalidOperationException())
        | false, Some RecoveryStoreFailure.TechnicalMutationUnknown
        | false, Some RecoveryStoreFailure.StoreUnavailable ->
            Ok(AdmittedExecution.CommitOutcomeUnknown operationId)
        | false, Some RecoveryStoreFailure.CancelledBeforeCommit ->
            failedBeforeCommit state operationId attemptId CoreFailure.StoreUnavailable
        | false, Some failure -> Error failure
        | false, None -> decideAndPersist state operation attemptId today decide

    let executeAdmitted state settings operation attemptId today decide =
        let result =
            lock state.Gate (fun () ->
                let operationId = (Operation.request operation).OperationId

                match Map.tryFind operationId state.Revocations with
                | Some _ ->
                    settleConfirmed state operationId attemptId "REVOKED_BEFORE_EXECUTION"
                    Ok(AdmittedExecution.RevokedBeforeExecution SettlementConfirmation.Confirmed)
                | None -> configuredExecution state settings operation attemptId today decide)

        Task.FromResult result

    let dismiss
        (state: RecoveryStoreStateData)
        (settings: RecoveryStoreSettings)
        operationId
        requestSha256
        =
        let result =
            lock state.Gate (fun () ->
                match settings.DismissFailure with
                | Some failure -> Error failure
                | None ->
                    match Map.tryFind operationId state.Accepted with
                    | Some value -> Ok(RecoveryDismissal.ObservedAccepted value)
                    | None ->
                        match
                            Map.tryFind operationId state.Values,
                            Map.tryFind operationId state.Revocations
                        with
                        | None, Some revoked -> Ok(RecoveryDismissal.RevokedTombstone revoked)
                        | None, None -> Error RecoveryStoreFailure.NotFound
                        | Some value, _ when value.RequestSha256 <> requestSha256 ->
                            Error RecoveryStoreFailure.IdempotencyConflict
                        | Some value, _ ->
                            match value.Lifecycle with
                            | PreparationLifecycle.Unsubmitted ->
                                let revoked =
                                    {
                                        OperationId = operationId
                                        CanonicalRequestFormat = value.CanonicalRequestFormat
                                        RequestSha256 = value.RequestSha256
                                        RevokedAt = DateTimeOffset.UtcNow
                                        Reason = "DISMISSED_BY_OPERATOR"
                                    }

                                state.Revocations <- Map.add operationId revoked state.Revocations

                                let dismissed =
                                    { value with
                                        Lifecycle =
                                            PreparationLifecycle.Dismissed revoked.RevokedAt
                                    }
                                    |> RecoveryStoreState.update state

                                Ok(RecoveryDismissal.Dismissed dismissed)
                            | PreparationLifecycle.Dismissed _ ->
                                Ok(RecoveryDismissal.AlreadyDismissed value)
                            | PreparationLifecycle.SubmissionStarted _ ->
                                Ok(RecoveryDismissal.SubmissionAlreadyStarted value))

        Task.FromResult result
