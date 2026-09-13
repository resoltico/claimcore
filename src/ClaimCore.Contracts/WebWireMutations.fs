namespace ClaimCore.Contracts

open System.Text.Json
open ClaimCore.Application

module internal WebWireMutations =
    let private definiteExecution (writer: Utf8JsonWriter) =
        function
        | DefiniteExecution.Accepted receipt ->
            writer.WriteStartObject()
            writer.WriteString("tag", "ACCEPTED")
            writer.WritePropertyName("receipt")
            WebWireValues.receipt writer receipt
            writer.WriteEndObject()
        | DefiniteExecution.ExecutionRejected(operationId, rejection) ->
            writer.WriteStartObject()
            writer.WriteString("tag", "REJECTED")
            writer.WriteString("operationId", operationId)
            writer.WritePropertyName("rejection")
            CliWireValues.rejection writer rejection
            writer.WriteEndObject()
        | DefiniteExecution.FailedBeforeCommit(operationId, fault) ->
            writer.WriteStartObject()
            writer.WriteString("tag", "FAILED_BEFORE_COMMIT")
            writer.WriteString("operationId", operationId)
            writer.WritePropertyName("fault")
            CliWireValues.fault writer fault
            writer.WriteEndObject()

    let private settlement =
        function
        | SettlementConfirmation.Confirmed -> "CONFIRMED"
        | SettlementConfirmation.Unconfirmed -> "UNCONFIRMED"

    let private acceptedPreparation (writer: Utf8JsonWriter) details receipt =
        WebWireQueries.outcome writer "OBSERVED_ACCEPTED" (fun () ->
            writer.WriteStartObject()
            writer.WritePropertyName("details")
            WebWireValues.preparationDetails writer details
            writer.WritePropertyName("receipt")
            WebWireValues.receipt writer receipt
            writer.WriteEndObject())

    let private retainedPreparation (writer: Utf8JsonWriter) details rejection =
        WebWireQueries.outcome writer "RETAINED_FOR_RECOVERY" (fun () ->
            writer.WriteStartObject()
            writer.WritePropertyName("details")
            WebWireValues.preparationDetails writer details
            writer.WritePropertyName("rejection")
            CliWireValues.rejection writer rejection
            writer.WriteEndObject())

    let prepare (writer: Utf8JsonWriter) (value: PrepareOutcome) =
        match value with
        | PrepareOutcome.Prepared(details, review) ->
            WebWireQueries.outcome writer "PREPARED" (fun () ->
                writer.WriteStartObject()
                writer.WritePropertyName("details")
                WebWireValues.preparationDetails writer details
                writer.WritePropertyName("review")
                WebWireValues.review writer review
                writer.WriteEndObject())
        | PrepareOutcome.ObservedAccepted(details, receipt) ->
            acceptedPreparation writer details receipt
        | PrepareOutcome.RetainedForRecovery(details, rejection) ->
            retainedPreparation writer details rejection
        | PrepareOutcome.PrepareRejected(operationId, rejection) ->
            WebWireQueries.outcome writer "REJECTED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("operationId", operationId)
                writer.WritePropertyName("rejection")
                CliWireValues.rejection writer rejection
                writer.WriteEndObject())
        | PrepareOutcome.PrepareFailed(operationId, fault) ->
            WebWireQueries.outcome writer "FAILED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("operationId", operationId)
                writer.WritePropertyName("fault")
                CliWireValues.fault writer fault
                writer.WriteEndObject())
        | PrepareOutcome.CancelledBeforeAdmission operationId ->
            WebWireQueries.outcome writer "CANCELLED_BEFORE_ADMISSION" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("operationId", operationId)
                writer.WriteEndObject())
        | PrepareOutcome.PreparationStateUnknown(operationId, requestSha256, fault) ->
            WebWireQueries.outcome writer "PREPARATION_STATE_UNKNOWN" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("operationId", operationId)
                writer.WriteString("requestSha256", requestSha256)
                writer.WritePropertyName("fault")
                CliWireValues.fault writer fault
                writer.WriteEndObject())

    let private completed
        (writer: Utf8JsonWriter)
        (preparation: PreparationSummary)
        (attemptId: System.Guid)
        (execution: DefiniteExecution)
        (settled: SettlementConfirmation)
        =
        WebWireQueries.outcome writer "COMPLETED" (fun () ->
            writer.WriteStartObject()
            writer.WritePropertyName("preparation")
            CliWireValues.preparationSummary writer preparation
            writer.WriteString("attemptId", attemptId)
            writer.WritePropertyName("execution")
            definiteExecution writer execution
            writer.WriteString("settlement", settlement settled)
            writer.WriteEndObject())

    let private beforeAttempt
        (writer: Utf8JsonWriter)
        (tag: string)
        (preparation: PreparationSummary option)
        (writeReason: unit -> unit)
        =
        WebWireQueries.outcome writer tag (fun () ->
            writer.WriteStartObject()
            writer.WritePropertyName("preparation")

            match preparation with
            | Some value -> CliWireValues.preparationSummary writer value
            | None -> writer.WriteNullValue()

            writeReason ()
            writer.WriteEndObject())

    let resolve (writer: Utf8JsonWriter) (value: ResolveOutcome) =
        match value with
        | ResolveOutcome.ResolveObservedAccepted receipt ->
            WebWireQueries.outcome writer "OBSERVED_ACCEPTED" (fun () ->
                writer.WriteStartObject()
                writer.WritePropertyName("receipt")
                WebWireValues.receipt writer receipt
                writer.WriteEndObject())
        | ResolveOutcome.ResolveCompleted(preparation, attemptId, execution, settled) ->
            completed writer preparation attemptId execution settled
        | ResolveOutcome.RefusedBeforeAttempt(preparation, rejection) ->
            beforeAttempt writer "REFUSED_BEFORE_ATTEMPT" preparation (fun () ->
                writer.WritePropertyName("rejection")
                CliWireValues.recoveryRejection writer rejection)
        | ResolveOutcome.ResolveFailedBeforeAttempt(preparation, fault) ->
            beforeAttempt writer "FAILED_BEFORE_ATTEMPT" preparation (fun () ->
                writer.WritePropertyName("fault")
                CliWireValues.fault writer fault)
        | ResolveOutcome.ResolveCancelledBeforeAdmission operationId ->
            WebWireQueries.outcome writer "CANCELLED_BEFORE_ADMISSION" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("operationId", operationId)
                writer.WriteEndObject())
        | ResolveOutcome.ResolveCancelledBeforeAttempt preparation ->
            WebWireQueries.outcome writer "CANCELLED_BEFORE_ATTEMPT" (fun () ->
                writer.WriteStartObject()
                writer.WritePropertyName("preparation")
                CliWireValues.preparationSummary writer preparation
                writer.WriteEndObject())
        | ResolveOutcome.ResolveAttemptAdmissionUnknown(preparation, fault) ->
            WebWireQueries.outcome writer "ATTEMPT_ADMISSION_UNKNOWN" (fun () ->
                writer.WriteStartObject()
                writer.WritePropertyName("preparation")
                CliWireValues.preparationSummary writer preparation
                writer.WritePropertyName("fault")
                CliWireValues.fault writer fault
                writer.WriteEndObject())
        | ResolveOutcome.ResolveAttemptUnresolved(preparation, attemptId, fault) ->
            WebWireQueries.outcome writer "ATTEMPT_UNRESOLVED" (fun () ->
                writer.WriteStartObject()
                writer.WritePropertyName("preparation")
                CliWireValues.preparationSummary writer preparation
                writer.WriteString("attemptId", attemptId)
                writer.WritePropertyName("fault")
                CliWireValues.fault writer fault
                writer.WriteEndObject())

    let dismiss (writer: Utf8JsonWriter) (value: RecoveryDismissOutcome) =
        let details tag item =
            WebWireQueries.outcome writer tag (fun () ->
                WebWireValues.preparationDetails writer item)

        match value with
        | RecoveryDismissOutcome.DismissedPreparation item -> details "DISMISSED" item
        | RecoveryDismissOutcome.AlreadyDismissedPreparation item ->
            details "ALREADY_DISMISSED" item
        | RecoveryDismissOutcome.DismissNotFound operationId ->
            WebWireQueries.outcome writer "NOT_FOUND" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("operationId", operationId)
                writer.WriteEndObject())
        | RecoveryDismissOutcome.DismissRefused(found, rejection) ->
            WebWireQueries.outcome writer "REFUSED" (fun () ->
                writer.WriteStartObject()
                writer.WritePropertyName("details")

                match found with
                | Some item -> WebWireValues.preparationDetails writer item
                | None -> writer.WriteNullValue()

                writer.WritePropertyName("rejection")
                CliWireValues.recoveryRejection writer rejection
                writer.WriteEndObject())
        | RecoveryDismissOutcome.DismissFailed fault ->
            WebWireQueries.outcome writer "FAILED" (fun () -> CliWireValues.fault writer fault)
        | RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId ->
            WebWireQueries.outcome writer "CANCELLED_BEFORE_ADMISSION" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("operationId", operationId)
                writer.WriteEndObject())
        | RecoveryDismissOutcome.DismissStateUnknown(operationId, requestSha256, fault) ->
            WebWireQueries.outcome writer "DISMISS_STATE_UNKNOWN" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("operationId", operationId)
                writer.WriteString("requestSha256", requestSha256)
                writer.WritePropertyName("fault")
                CliWireValues.fault writer fault
                writer.WriteEndObject())

    let importRetain (writer: Utf8JsonWriter) (value: RecoveryImportRetainOutcome) =
        let details tag item =
            WebWireQueries.outcome writer tag (fun () ->
                WebWireValues.preparationDetails writer item)

        match value with
        | RecoveryImportRetainOutcome.RetainedPreparation item -> details "RETAINED" item
        | RecoveryImportRetainOutcome.ExistingPreparation item -> details "EXISTING" item
        | RecoveryImportRetainOutcome.ImportRejected rejection ->
            WebWireQueries.outcome writer "REJECTED" (fun () ->
                CliWireValues.recoveryRejection writer rejection)
        | RecoveryImportRetainOutcome.ImportFailed fault ->
            WebWireQueries.outcome writer "FAILED" (fun () -> CliWireValues.fault writer fault)
        | RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission ->
            WebWireQueries.outcome writer "CANCELLED_BEFORE_ADMISSION" writer.WriteNullValue
        | RecoveryImportRetainOutcome.RetainStateUnknown(kind, digest, operationId, fault) ->
            WebWireQueries.outcome writer "RETAIN_STATE_UNKNOWN" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("artifactKind", WireTokens.webArtifactKind kind)
                writer.WriteString("sourceSha256", digest)

                match operationId with
                | Some value -> writer.WriteString("operationId", value)
                | None -> writer.WriteNull("operationId")

                writer.WritePropertyName("fault")
                CliWireValues.fault writer fault
                writer.WriteEndObject())
