namespace ClaimCore.Contracts

open System.Text.Json
open ClaimCore.Application

module internal WebWireSubmission =
    let private sharedOutcome =
        function
        | SubmissionOutcome.ObservedAccepted receipt ->
            Some(ResolveOutcome.ResolveObservedAccepted receipt)
        | SubmissionOutcome.Completed(preparation, attemptId, execution, settlement) ->
            Some(ResolveOutcome.ResolveCompleted(preparation, attemptId, execution, settlement))
        | SubmissionOutcome.FailedBeforeAttempt(preparation, fault) ->
            Some(ResolveOutcome.ResolveFailedBeforeAttempt(preparation, fault))
        | SubmissionOutcome.CancelledBeforeAdmission operationId ->
            Some(ResolveOutcome.ResolveCancelledBeforeAdmission operationId)
        | SubmissionOutcome.CancelledBeforeAttempt preparation ->
            Some(ResolveOutcome.ResolveCancelledBeforeAttempt preparation)
        | SubmissionOutcome.AttemptAdmissionUnknown(preparation, fault) ->
            Some(ResolveOutcome.ResolveAttemptAdmissionUnknown(preparation, fault))
        | SubmissionOutcome.AttemptUnresolved(preparation, attemptId, fault) ->
            Some(ResolveOutcome.ResolveAttemptUnresolved(preparation, attemptId, fault))
        | _ -> None

    let write (writer: Utf8JsonWriter) value =
        match value with
        | SubmissionOutcome.RejectedBeforeAttempt(preparation, rejection) ->
            WebWireMutations.beforeAttempt writer "REFUSED_BEFORE_ATTEMPT" preparation (fun () ->
                writer.WritePropertyName("rejection")
                CliWireValues.rejection writer rejection)
        | SubmissionOutcome.PreparationStateUnknown(operationId, requestSha256, fault) ->
            WebWireQueries.outcome writer "PREPARATION_STATE_UNKNOWN" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("operationId", operationId)
                writer.WriteString("requestSha256", requestSha256)
                writer.WritePropertyName("fault")
                CliWireValues.fault writer fault
                writer.WriteEndObject())
        | other ->
            other
            |> sharedOutcome
            |> Option.defaultWith (fun () -> invalidOp "Submission outcome is not mapped.")
            |> WebWireMutations.resolve writer
