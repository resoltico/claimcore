namespace ClaimCore.Contracts

open System.Text.Json
open ClaimCore.Application

module internal CliWireMutations =
    let private settlement (value: SettlementConfirmation) =
        match value with
        | SettlementConfirmation.Confirmed -> "CONFIRMED"
        | SettlementConfirmation.Unconfirmed -> "UNCONFIRMED"

    let private definiteExecution (writer: Utf8JsonWriter) (value: DefiniteExecution) =
        match value with
        | DefiniteExecution.Accepted receipt ->
            writer.WriteStartObject()
            writer.WriteString("kind", "accepted")
            writer.WritePropertyName("receipt")
            CliWireValues.receipt false writer receipt
            writer.WriteEndObject()
        | DefiniteExecution.ExecutionRejected(operationId, rejection) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "rejected")
            writer.WriteString("operationId", operationId)
            writer.WritePropertyName("rejection")
            CliWireValues.rejection writer rejection
            writer.WriteEndObject()
        | DefiniteExecution.FailedBeforeCommit(operationId, fault) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "failedBeforeCommit")
            writer.WriteString("operationId", operationId)
            writer.WritePropertyName("fault")
            CliWireValues.fault writer fault
            writer.WriteEndObject()

    let private optionalPreparation (writer: Utf8JsonWriter) (value: PreparationSummary option) =
        match value with
        | Some value -> CliWireValues.preparationSummary writer value
        | None -> writer.WriteNullValue()

    let private completed
        (writer: Utf8JsonWriter)
        (preparation: PreparationSummary)
        (attemptId: System.Guid)
        (execution: DefiniteExecution)
        (confirmation: SettlementConfirmation)
        =
        writer.WriteStartObject()
        writer.WriteString("kind", "completed")
        writer.WritePropertyName("preparation")
        CliWireValues.preparationSummary writer preparation
        writer.WriteString("attemptId", attemptId)
        writer.WritePropertyName("execution")
        definiteExecution writer execution
        writer.WriteString("settlement", settlement confirmation)
        writer.WriteEndObject()

    let private beforeAttempt
        (writer: Utf8JsonWriter)
        (kind: string)
        (preparation: PreparationSummary option)
        (detailName: string)
        (detail: Utf8JsonWriter -> unit)
        =
        writer.WriteStartObject()
        writer.WriteString("kind", kind)
        writer.WritePropertyName("preparation")
        optionalPreparation writer preparation
        writer.WritePropertyName(detailName)
        detail writer
        writer.WriteEndObject()

    let private operationOnly (writer: Utf8JsonWriter) (kind: string) (operationId: System.Guid) =
        writer.WriteStartObject()
        writer.WriteString("kind", kind)
        writer.WriteString("operationId", operationId)
        writer.WriteEndObject()

    let private preparationOnly
        (writer: Utf8JsonWriter)
        (kind: string)
        (preparation: PreparationSummary)
        =
        writer.WriteStartObject()
        writer.WriteString("kind", kind)
        writer.WritePropertyName("preparation")
        CliWireValues.preparationSummary writer preparation
        writer.WriteEndObject()

    let private preparationFailure
        (writer: Utf8JsonWriter)
        (kind: string)
        (preparation: PreparationSummary)
        (fault: CoreFault)
        =
        writer.WriteStartObject()
        writer.WriteString("kind", kind)
        writer.WritePropertyName("preparation")
        CliWireValues.preparationSummary writer preparation
        writer.WritePropertyName("fault")
        CliWireValues.fault writer fault
        writer.WriteEndObject()

    let private unresolved
        (writer: Utf8JsonWriter)
        (kind: string)
        (preparation: PreparationSummary)
        (attemptId: System.Guid)
        (fault: CoreFault)
        =
        writer.WriteStartObject()
        writer.WriteString("kind", kind)
        writer.WritePropertyName("preparation")
        CliWireValues.preparationSummary writer preparation
        writer.WriteString("attemptId", attemptId)
        writer.WritePropertyName("fault")
        CliWireValues.fault writer fault
        writer.WriteEndObject()

    let private detailsOutcome
        (writer: Utf8JsonWriter)
        (kind: string)
        (details: PreparationDetails)
        =
        writer.WriteStartObject()
        writer.WriteString("kind", kind)
        writer.WritePropertyName("details")
        CliWireValues.preparationDetails writer details
        writer.WriteEndObject()

    let submission (writer: Utf8JsonWriter) (outcome: SubmissionOutcome) =
        match outcome with
        | SubmissionOutcome.ObservedAccepted receipt ->
            writer.WriteStartObject()
            writer.WriteString("kind", "observedAccepted")
            writer.WritePropertyName("receipt")
            CliWireValues.receipt false writer receipt
            writer.WriteEndObject()
        | SubmissionOutcome.Completed(preparation, attemptId, execution, confirmation) ->
            completed writer preparation attemptId execution confirmation
        | SubmissionOutcome.RejectedBeforeAttempt(preparation, rejection) ->
            beforeAttempt writer "rejectedBeforeAttempt" preparation "rejection" (fun output ->
                CliWireValues.rejection output rejection)
        | SubmissionOutcome.FailedBeforeAttempt(preparation, fault) ->
            beforeAttempt writer "failedBeforeAttempt" preparation "fault" (fun output ->
                CliWireValues.fault output fault)
        | SubmissionOutcome.PreparationStateUnknown(operationId, requestSha256, fault) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "preparationStateUnknown")
            writer.WriteString("operationId", operationId)
            writer.WriteString("requestSha256", requestSha256)
            writer.WritePropertyName("fault")
            CliWireValues.fault writer fault
            writer.WriteEndObject()
        | SubmissionOutcome.CancelledBeforeAdmission operationId ->
            operationOnly writer "cancelledBeforeAdmission" operationId
        | SubmissionOutcome.CancelledBeforeAttempt preparation ->
            preparationOnly writer "cancelledBeforeAttempt" preparation
        | SubmissionOutcome.AttemptAdmissionUnknown(preparation, fault) ->
            preparationFailure writer "attemptAdmissionUnknown" preparation fault
        | SubmissionOutcome.AttemptUnresolved(preparation, attemptId, fault) ->
            unresolved writer "attemptUnresolved" preparation attemptId fault

    let resolve (writer: Utf8JsonWriter) (outcome: ResolveOutcome) =
        match outcome with
        | ResolveOutcome.ResolveObservedAccepted receipt ->
            writer.WriteStartObject()
            writer.WriteString("kind", "observedAccepted")
            writer.WritePropertyName("receipt")
            CliWireValues.receipt false writer receipt
            writer.WriteEndObject()
        | ResolveOutcome.ResolveCompleted(preparation, attemptId, execution, confirmation) ->
            completed writer preparation attemptId execution confirmation
        | ResolveOutcome.RefusedBeforeAttempt(preparation, rejection) ->
            beforeAttempt writer "refusedBeforeAttempt" preparation "rejection" (fun output ->
                CliWireValues.recoveryRejection output rejection)
        | ResolveOutcome.ResolveFailedBeforeAttempt(preparation, fault) ->
            beforeAttempt writer "failedBeforeAttempt" preparation "fault" (fun output ->
                CliWireValues.fault output fault)
        | ResolveOutcome.ResolveCancelledBeforeAdmission operationId ->
            operationOnly writer "cancelledBeforeAdmission" operationId
        | ResolveOutcome.ResolveCancelledBeforeAttempt preparation ->
            preparationOnly writer "cancelledBeforeAttempt" preparation
        | ResolveOutcome.ResolveAttemptAdmissionUnknown(preparation, fault) ->
            preparationFailure writer "attemptAdmissionUnknown" preparation fault
        | ResolveOutcome.ResolveAttemptUnresolved(preparation, attemptId, fault) ->
            unresolved writer "attemptUnresolved" preparation attemptId fault

    let dismiss (writer: Utf8JsonWriter) (outcome: RecoveryDismissOutcome) =
        match outcome with
        | RecoveryDismissOutcome.DismissedPreparation details ->
            detailsOutcome writer "dismissed" details
        | RecoveryDismissOutcome.AlreadyDismissedPreparation details ->
            detailsOutcome writer "alreadyDismissed" details
        | RecoveryDismissOutcome.DismissNotFound operationId ->
            operationOnly writer "notFound" operationId
        | RecoveryDismissOutcome.DismissRefused(details, rejection) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "refused")
            writer.WritePropertyName("details")

            match details with
            | Some value -> CliWireValues.preparationDetails writer value
            | None -> writer.WriteNullValue()

            writer.WritePropertyName("rejection")
            CliWireValues.recoveryRejection writer rejection
            writer.WriteEndObject()
        | RecoveryDismissOutcome.DismissFailed fault -> CliWireQueries.failed writer fault
        | RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId ->
            operationOnly writer "cancelledBeforeAdmission" operationId
        | RecoveryDismissOutcome.DismissStateUnknown(operationId, digest, fault) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "dismissStateUnknown")
            writer.WriteString("operationId", operationId)
            writer.WriteString("requestSha256", digest)
            writer.WritePropertyName("fault")
            CliWireValues.fault writer fault
            writer.WriteEndObject()
