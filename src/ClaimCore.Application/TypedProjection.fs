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
        match failure with
        | RecoveryStoreFailure.InvalidInput _ -> CoreFault.RecoveryResponseInvalid
        | RecoveryStoreFailure.IdempotencyConflict -> CoreFault.RecoveryContentConflict
        | RecoveryStoreFailure.NotFound -> CoreFault.RecoveryPreparationMissing
        | RecoveryStoreFailure.CapacityExceeded -> CoreFault.RecoveryCapacityExhausted
        | RecoveryStoreFailure.SchemaMismatch -> CoreFault.RecoverySchemaMismatch
        | RecoveryStoreFailure.StoreUnavailable -> CoreFault.RecoveryStoreUnavailable
        | RecoveryStoreFailure.StoreCorrupt -> CoreFault.RecoveryStoreIntegrityError
        | RecoveryStoreFailure.ReadCancelled -> CoreFault.RecoveryReadCancelled
        | RecoveryStoreFailure.CancelledBeforeCommit -> CoreFault.RecoveryMutationCancelled
        | RecoveryStoreFailure.TechnicalMutationUnknown -> CoreFault.RecoveryMutationUnknown

    let runtimeContext (context: BusinessContext) : RuntimeContext =
        {
            ProductVersion = BuildIdentity.current.Version
            EffectiveBusinessDate = context.EffectiveBusinessDate
            TimeZoneId = context.TimeZoneId
        }

    let description (clock: IBusinessTime) : CoreDescription =
        {
            Contract = SemanticContract.current
            SemanticFingerprint = SemanticContract.fingerprint SemanticContract.current
            Runtime = runtimeContext (clock.Capture())
        }

    let private correctionValues correction =
        let registration =
            match correction.Registration with
            | RegistrationCorrection.Keep -> [ "registration.action", "KEEP" ]
            | RegistrationCorrection.Replace value ->
                [
                    "registration.action", "REPLACE"
                    "incidentDate", value.IncidentDate
                    "incidentNotificationDate", value.IncidentNotificationDate
                    "incidentCountry", value.IncidentCountry
                    "claimantName", value.ClaimantName
                    "insurerName", value.InsurerName
                    "claimedAmount", value.ClaimedAmount
                    "claimedCurrency", value.ClaimedCurrency
                ]

        let decision =
            match correction.Decision with
            | DecisionCorrection.Keep -> [ "decision.action", "KEEP" ]
            | DecisionCorrection.Clear -> [ "decision.action", "CLEAR" ]
            | DecisionCorrection.Replace value ->
                [
                    "decision.action", "REPLACE"
                    "paymentDecisionDate", value.PaymentDecisionDate
                    "payableAmount", value.PayableAmount
                    "payableCurrency", value.PayableCurrency
                ]

        let payment =
            match correction.Payment with
            | PaymentCorrection.Keep -> [ "payment.action", "KEEP" ]
            | PaymentCorrection.Clear -> [ "payment.action", "CLEAR" ]
            | PaymentCorrection.Replace value ->
                [ "payment.action", "REPLACE"; "paymentDate", value ]

        registration @ decision @ payment

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
        | Command.CorrectCase correction -> correctionValues correction
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

        match (CommandDefinitions.forKind (Commands.kind request.Command)).Inputs with
        | CommandInputShape.Fields fields ->
            fields
            |> List.map (fun field -> field.FieldName, Map.find field.FieldName byName)
        | CommandInputShape.CorrectionGroups _ -> values request.Command

    let private preparationState authority lifecycle =
        match authority with
        | RecoveryAuthority.RevokedAuthority -> PreparationState.Revoked
        | RecoveryAuthority.PendingAuthority
        | RecoveryAuthority.AcceptedAuthority ->
            match lifecycle with
            | PreparationLifecycle.Unsubmitted -> PreparationState.Unsubmitted
            | PreparationLifecycle.SubmissionStarted _ -> PreparationState.SubmissionStarted
            | PreparationLifecycle.Dismissed _ -> PreparationState.Dismissed

    let private preparationActions authority lifecycle =
        match authority with
        | RecoveryAuthority.AcceptedAuthority
        | RecoveryAuthority.RevokedAuthority -> [ RecoveryAction.Export ]
        | RecoveryAuthority.PendingAuthority ->
            match lifecycle with
            | PreparationLifecycle.Unsubmitted ->
                [ RecoveryAction.Resolve; RecoveryAction.Dismiss; RecoveryAction.Export ]
            | PreparationLifecycle.SubmissionStarted _ ->
                [ RecoveryAction.Resolve; RecoveryAction.Dismiss; RecoveryAction.Export ]
            | PreparationLifecycle.Dismissed _ -> [ RecoveryAction.Export ]

    let private inferredAuthority lifecycle =
        match lifecycle with
        | PreparationLifecycle.Dismissed _ -> RecoveryAuthority.RevokedAuthority
        | PreparationLifecycle.Unsubmitted
        | PreparationLifecycle.SubmissionStarted _ -> RecoveryAuthority.PendingAuthority

    let private summaryWithAuthority
        includeDigest
        authority
        (preparation: RetainedPreparation)
        : Result<PreparationSummary, CoreFault> =
        let request =
            RequestRecord.decode
                SemanticContract.current.RequestByteLimit
                preparation.CanonicalRequest

        match request with
        | Error _ -> Error(CoreFault.RetainedCanonicalInvalid)
        | Ok decoded ->
            Ok
                {
                    OperationId = preparation.OperationId
                    CaseReference = decoded.CaseReference
                    Command = Commands.kind decoded.Command
                    PreparedAt = preparation.PreparedAt
                    State = preparationState authority preparation.Lifecycle
                    Authority = authority
                    RequestSha256 =
                        if includeDigest then
                            Some preparation.RequestSha256
                        else
                            None
                    AvailableActions = preparationActions authority preparation.Lifecycle
                }

    let summary includeDigest (preparation: RetainedPreparation) =
        summaryWithAuthority includeDigest (inferredAuthority preparation.Lifecycle) preparation

    let summaryWithKnownAuthority includeDigest authority preparation =
        summaryWithAuthority includeDigest authority preparation

    let revokedOperation (value: OperationRevocation) : RevokedOperation =
        {
            OperationId = value.OperationId
            RevokedAt = value.RevokedAt
            Reason = value.Reason
        }

    let acceptedDetails (details: PreparationDetails) : PreparationDetails =
        { details with
            Summary =
                { details.Summary with
                    Authority = RecoveryAuthority.AcceptedAuthority
                    AvailableActions = [ RecoveryAction.Export ]
                }
        }

    let private emptyAttemptPage = { Items = []; NextCursor = None }

    let private detailsWithAuthority
        (preparation: RetainedPreparation)
        (attempts: PreparationAttemptPage)
        (authority: RecoveryAuthority)
        : Result<PreparationDetails, CoreFault> =
        match
            RequestRecord.decode
                SemanticContract.current.RequestByteLimit
                preparation.CanonicalRequest,
            summaryWithKnownAuthority true authority preparation
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
                        | PreparingContractKind.CanonicalRecordV3 -> "CANONICAL_RECORD_V3"
                        | PreparingContractKind.SemanticCoreV1 -> "SEMANTIC_CORE_V1"
                    Attempts = attempts
                }
        | Error _, _
        | _, Error _ -> Error(CoreFault.RetainedCanonicalInvalid)

    let detailsWithAttempts
        (preparation: RetainedPreparation)
        (attempts: PreparationAttemptPage)
        : Result<PreparationDetails, CoreFault> =
        detailsWithAuthority preparation attempts (inferredAuthority preparation.Lifecycle)

    let detailsWithKnownAuthority preparation attempts authority =
        detailsWithAuthority preparation attempts authority

    let details (preparation: RetainedPreparation) : Result<PreparationDetails, CoreFault> =
        detailsWithAttempts preparation emptyAttemptPage
