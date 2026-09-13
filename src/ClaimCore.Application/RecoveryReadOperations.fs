namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.RecordFormat

module internal RecoveryReadOperations =
    let private invalidFault message : CoreFault =
        {
            Code = FaultCode.RecoveryIntegrityError
            Message = message
            Action = RecommendedAction.StopAndInvestigate
        }

    let private projectPage (page: RecoveryStorePage) =
        let summaries = page.Items |> List.map (TypedProjection.summary false)

        match
            summaries
            |> List.tryPick (function
                | Error fault -> Some fault
                | Ok _ -> None)
        with
        | Some fault -> RecoveryQueryOutcome.RecoveryFailed fault
        | None ->
            RecoveryQueryOutcome.RecoverySucceeded
                {
                    Items = summaries |> List.choose Result.toOption
                    NextCursor = page.NextAfter |> Option.map RecoveryCursorCodec.encode
                }

    let list
        (recovery: IRecoveryStore)
        (afterCursor: string option)
        (limit: int)
        (cancellationToken: CancellationToken)
        : Task<RecoveryQueryOutcome<RecoveryPage>> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(RecoveryQueryOutcome.RecoveryCancelled)
        elif limit < 1 || limit > SemanticContract.current.MaximumPageSize then
            Task.FromResult(RecoveryQueryOutcome.RecoveryRejected(RecoverySupport.invalid "limit"))
        else
            let after =
                afterCursor
                |> Option.map (RecoveryCursorCodec.decode >> Result.map Some)
                |> Option.defaultValue (Ok None)

            match after with
            | Error _ ->
                Task.FromResult(
                    RecoveryQueryOutcome.RecoveryRejected(RecoverySupport.invalid "cursor")
                )
            | Ok cursor ->
                task {
                    match! recovery.List(cursor, limit, cancellationToken) with
                    | Error RecoveryStoreFailure.ReadCancelled ->
                        return RecoveryQueryOutcome.RecoveryCancelled
                    | Error failure ->
                        return
                            RecoveryQueryOutcome.RecoveryFailed(
                                TypedProjection.recoveryFault failure
                            )
                    | Ok page ->
                        if cancellationToken.IsCancellationRequested then
                            return RecoveryQueryOutcome.RecoveryCancelled
                        else
                            return projectPage page
                }

    let private inspectRetained
        (store: IClaimStore)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        (retained: RetainedPreparation)
        : Task<RecoveryQueryOutcome<Lookup<RecoveryDetails, Guid>>> =
        task {
            match TypedProjection.details retained with
            | Error fault -> return RecoveryQueryOutcome.RecoveryFailed fault
            | Ok details ->
                match! TypedQueries.observe store operationId cancellationToken with
                | QueryOutcome.Succeeded observation ->
                    if cancellationToken.IsCancellationRequested then
                        return RecoveryQueryOutcome.RecoveryCancelled
                    else
                        let inspected =
                            match observation with
                            | Lookup.Found _ -> TypedProjection.acceptedDetails details
                            | Lookup.NotFound _ -> details

                        return
                            RecoveryQueryOutcome.RecoverySucceeded(
                                Lookup.Found
                                    {
                                        Preparation = inspected
                                        Observation = observation
                                    }
                            )
                | QueryOutcome.Failed fault -> return RecoveryQueryOutcome.RecoveryFailed fault
                | QueryOutcome.Cancelled -> return RecoveryQueryOutcome.RecoveryCancelled
                | QueryOutcome.Rejected rejection ->
                    return RecoveryQueryOutcome.RecoveryFailed(invalidFault rejection.Message)
        }

    let inspect
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        : Task<RecoveryQueryOutcome<Lookup<RecoveryDetails, Guid>>> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(RecoveryQueryOutcome.RecoveryCancelled)
        elif operationId = Guid.Empty then
            Task.FromResult(
                RecoveryQueryOutcome.RecoveryRejected(RecoverySupport.invalid "operationId")
            )
        else
            task {
                match! recovery.Get(operationId, cancellationToken) with
                | Error RecoveryStoreFailure.ReadCancelled ->
                    return RecoveryQueryOutcome.RecoveryCancelled
                | Error failure ->
                    return
                        RecoveryQueryOutcome.RecoveryFailed(TypedProjection.recoveryFault failure)
                | Ok None ->
                    return RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId)
                | Ok(Some retained) ->
                    return! inspectRetained store operationId cancellationToken retained
            }

    let resolve
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
        (operationId: Guid)
        (requestSha256: string)
        (cancellationToken: CancellationToken)
        : Task<ResolveOutcome> =
        if operationId = Guid.Empty || not (RecoverySupport.validDigest requestSha256) then
            Task.FromResult(
                ResolveOutcome.RefusedBeforeAttempt(None, RecoverySupport.invalid "resolve")
            )
        else
            task {
                let! result =
                    TypedResolution.resolveRetained
                        store
                        recovery
                        clock
                        operationId
                        requestSha256
                        cancellationToken

                return RecoverySupport.resolveOutcome result
            }

    let private encodeExport
        (operationId: Guid)
        (lineage: Guid)
        (retained: RetainedPreparation)
        : Result<RecoveryExport, CoreFault> =
        match
            RequestRecord.decode SemanticContract.current.RequestByteLimit retained.CanonicalRequest
        with
        | Error _ ->
            Error(invalidFault "Retained canonical request bytes failed integrity validation.")
        | Ok _ ->
            let bytes =
                RecoveryEnvelope.encode
                    {
                        InstallationId = lineage
                        OperationId = retained.OperationId
                        ProtocolVersion = retained.CanonicalRequestFormat
                        RequestFingerprintVersion = RecordVersions.RequestFingerprint
                        RequestSha256 = retained.RequestSha256
                        CanonicalRequest = retained.CanonicalRequest
                    }

            Ok
                {
                    Bytes = bytes
                    FileName = "claimcore-recovery-" + operationId.ToString("D") + ".json"
                    MediaType = "application/vnd.claimcore.recovery+json"
                    RequestSha256 = retained.RequestSha256
                }

    let private exportRetained
        (recovery: IRecoveryStore)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        (retained: RetainedPreparation)
        : Task<RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>>> =
        task {
            match! recovery.InstallationLineage cancellationToken with
            | Error RecoveryStoreFailure.ReadCancelled ->
                return RecoveryQueryOutcome.RecoveryCancelled
            | Error failure ->
                return RecoveryQueryOutcome.RecoveryFailed(TypedProjection.recoveryFault failure)
            | Ok lineage ->
                let encoded = encodeExport operationId lineage retained

                if cancellationToken.IsCancellationRequested then
                    return RecoveryQueryOutcome.RecoveryCancelled
                else
                    match encoded with
                    | Error fault -> return RecoveryQueryOutcome.RecoveryFailed fault
                    | Ok artifact ->
                        return RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact)
        }

    let export
        (recovery: IRecoveryStore)
        (operationId: Guid)
        (requestSha256: string)
        (cancellationToken: CancellationToken)
        : Task<RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>>> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(RecoveryQueryOutcome.RecoveryCancelled)
        elif operationId = Guid.Empty || not (RecoverySupport.validDigest requestSha256) then
            Task.FromResult(
                RecoveryQueryOutcome.RecoveryRejected(RecoverySupport.invalid "export identity")
            )
        else
            task {
                match! recovery.Get(operationId, cancellationToken) with
                | Error RecoveryStoreFailure.ReadCancelled ->
                    return RecoveryQueryOutcome.RecoveryCancelled
                | Error failure ->
                    return
                        RecoveryQueryOutcome.RecoveryFailed(TypedProjection.recoveryFault failure)
                | Ok None ->
                    return RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId)
                | Ok(Some retained) when retained.RequestSha256 <> requestSha256 ->
                    return RecoveryQueryOutcome.RecoveryRejected RecoverySupport.conflict
                | Ok(Some _) when cancellationToken.IsCancellationRequested ->
                    return RecoveryQueryOutcome.RecoveryCancelled
                | Ok(Some retained) ->
                    return! exportRetained recovery operationId cancellationToken retained
            }
