namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Semantic projections shared by every typed endpoint. The module deliberately receives only
/// Domain/Application values; it has no JSON, SQL, or adapter dependency.
module internal TypedProjection =
    let rejection = CoreConversions.rejection
    let coreFault = CoreConversions.fault
    let currentCase = CoreConversions.currentCase
    let receipt = CoreConversions.receipt

    let recoveryFault (failure: RecoveryStoreFailure) : CoreFault =
        let code, message, action =
            match failure with
            | RecoveryStoreFailure.InvalidInput _ ->
                FaultCode.RecoveryIntegrityError,
                "Recovery storage returned an invalid technical response.",
                RecommendedAction.StopAndInvestigate
            | RecoveryStoreFailure.IdempotencyConflict ->
                FaultCode.TechnicalMutationUnknown,
                "Recovery identity conflicts with retained data.",
                RecommendedAction.StopAndInvestigate
            | RecoveryStoreFailure.NotFound ->
                FaultCode.RecoveryIntegrityError,
                "Recovery storage lost an expected preparation.",
                RecommendedAction.StopAndInvestigate
            | RecoveryStoreFailure.CapacityExceeded ->
                FaultCode.RecoveryCapacityExceeded,
                "Recovery capacity is exhausted.",
                RecommendedAction.StopAndInvestigate
            | RecoveryStoreFailure.SchemaMismatch ->
                FaultCode.SchemaMismatch,
                "Recovery storage is incompatible with this runtime.",
                RecommendedAction.StopAndInvestigate
            | RecoveryStoreFailure.StoreUnavailable ->
                FaultCode.StoreUnavailable,
                "Recovery storage was unavailable before completion was confirmed.",
                RecommendedAction.RetrySafe
            | RecoveryStoreFailure.StoreCorrupt ->
                FaultCode.RecoveryIntegrityError,
                "Recovery storage failed integrity validation.",
                RecommendedAction.StopAndInvestigate
            | RecoveryStoreFailure.ReadCancelled ->
                FaultCode.StoreUnavailable,
                "The recovery read was cancelled.",
                RecommendedAction.RetrySafe
            | RecoveryStoreFailure.CancelledBeforeCommit ->
                FaultCode.StoreUnavailable,
                "The technical mutation was cancelled before commit.",
                RecommendedAction.RetrySafe
            | RecoveryStoreFailure.TechnicalMutationUnknown ->
                FaultCode.TechnicalMutationUnknown,
                "The recovery-state mutation may have committed but could not be confirmed.",
                RecommendedAction.RecoverExact

        {
            Code = code
            Message = message
            Action = action
        }

    let runtimeContext (clock: IBusinessDate) : RuntimeContext =
        {
            ProductVersion = BuildIdentity.current.Version
            EffectiveBusinessDate = clock.Today()
            TimeZoneId = TimeZoneInfo.Local.Id
        }

    let description (clock: IBusinessDate) : CoreDescription =
        {
            Contract = SemanticContract.current
            SemanticFingerprint = SemanticContract.fingerprint SemanticContract.current
            Runtime = runtimeContext clock
        }

    let private values (command: Command) =
        match command with
        | Command.Open registration
        | Command.AmendRegistration registration ->
            [
                "incidentDate", registration.IncidentDate
                "incidentNotificationDate", registration.IncidentNotificationDate
                "incidentCountry", registration.IncidentCountry
                "claimantName", registration.ClaimantName
                "insurerName", registration.InsurerName
                "claimedAmount", registration.ClaimedAmount
                "claimedCurrency", registration.ClaimedCurrency
            ]
        | Command.Decide decision ->
            [
                "paymentDecisionDate", decision.PaymentDecisionDate
                "payableAmount", decision.PayableAmount
                "payableCurrency", decision.PayableCurrency
            ]
        | Command.RecordPayment paymentDate -> [ "paymentDate", paymentDate ]
        | Command.WithdrawDecision
        | Command.ClearPayment
        | Command.Close
        | Command.Reopen -> []

    let authoredValues (request: CommandRequest) =
        let byName = values request.Command |> Map.ofList

        CommandDefinitions.inputFields (Commands.kind request.Command)
        |> List.map (fun fieldName -> fieldName, Map.find fieldName byName)

    let private preparationState lifecycle =
        match lifecycle with
        | PreparationLifecycle.Unsubmitted -> PreparationState.Unsubmitted
        | PreparationLifecycle.SubmissionStarted _ -> PreparationState.SubmissionStarted
        | PreparationLifecycle.Dismissed _ -> PreparationState.Dismissed

    let private preparationActions lifecycle =
        match lifecycle with
        | PreparationLifecycle.Unsubmitted ->
            [ RecoveryAction.Resolve; RecoveryAction.Dismiss; RecoveryAction.Export ]
        | PreparationLifecycle.SubmissionStarted _ ->
            [ RecoveryAction.Resolve; RecoveryAction.Export ]
        | PreparationLifecycle.Dismissed _ -> [ RecoveryAction.Export ]

    let acceptedDetails (details: PreparationDetails) : PreparationDetails =
        { details with
            Summary =
                { details.Summary with
                    AvailableActions = [ RecoveryAction.Export ]
                }
        }

    let summary
        includeDigest
        (preparation: RetainedPreparation)
        : Result<PreparationSummary, CoreFault> =
        let request =
            RequestRecord.decode
                SemanticContract.current.RequestByteLimit
                preparation.CanonicalRequest

        match request with
        | Error _ ->
            Error(
                {
                    Code = FaultCode.RecoveryIntegrityError
                    Message = "Retained canonical request bytes failed integrity validation."
                    Action = RecommendedAction.StopAndInvestigate
                }
            )
        | Ok decoded ->
            Ok
                {
                    OperationId = preparation.OperationId
                    CaseReference = decoded.CaseReference
                    Command = Commands.kind decoded.Command
                    PreparedAt = preparation.PreparedAt
                    State = preparationState preparation.Lifecycle
                    RequestSha256 =
                        if includeDigest then
                            Some preparation.RequestSha256
                        else
                            None
                    AvailableActions = preparationActions preparation.Lifecycle
                }

    let details (preparation: RetainedPreparation) : Result<PreparationDetails, CoreFault> =
        match
            RequestRecord.decode
                SemanticContract.current.RequestByteLimit
                preparation.CanonicalRequest,
            summary true preparation
        with
        | Ok request, Ok preparationSummary ->
            Ok
                {
                    Summary = preparationSummary
                    ExpectedVersion = request.ExpectedVersion
                    AuthoredValues = authoredValues request
                    CanonicalCommandFormat = preparation.CanonicalRequestFormat
                    PreparingApplicationVersion = preparation.PreparingApplicationVersion
                    PreparingContractFingerprint = preparation.PreparingContractFingerprint
                    PreparingContractKind =
                        match preparation.PreparingContractKind with
                        | PreparingContractKind.LegacyUnclassified -> "LEGACY_UNCLASSIFIED"
                        | PreparingContractKind.SemanticCoreV1 -> "SEMANTIC_CORE_V1"
                    Attempts = preparation.Attempts
                    LegacyUncertainty = preparation.LegacyUncertainty
                }
        | Error _, _
        | _, Error _ ->
            Error(
                {
                    Code = FaultCode.RecoveryIntegrityError
                    Message = "Retained canonical request bytes failed integrity validation."
                    Action = RecommendedAction.StopAndInvestigate
                }
            )

    let review (clock: IBusinessDate) (before: Claim option) (proposed: Claim) : AdvisoryReview =
        let previous =
            before
            |> Option.map (
                Claim.view >> fun view -> FieldDefinitions.values view.Fields |> Map.ofList
            )
            |> Option.defaultValue Map.empty

        let next =
            proposed
            |> Claim.view
            |> fun view -> FieldDefinitions.values view.Fields |> Map.ofList

        let changes =
            FieldDefinitions.all
            |> List.choose (fun field ->
                let beforeValue = previous |> Map.tryFind field.Name |> Option.flatten
                let afterValue = next |> Map.tryFind field.Name |> Option.flatten

                if beforeValue = afterValue then
                    None
                else
                    Some
                        {
                            FieldName = field.Name
                            Before = beforeValue
                            After = afterValue
                        })

        {
            Before = before |> Option.map Claim.view
            Proposed = Claim.view proposed
            Changes = changes
            Context = runtimeContext clock
            IsAdvisory = true
        }
