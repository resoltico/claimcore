namespace ClaimCore.Cli

open System
open ClaimCore.Application

/// Only typed operation context, never diagnostic text or arbitrary JSON. Preserve an authored
/// identity over a conflicting returned identity; import retention can first reveal one here.
module ReplyRecoveryContext =
    let private summary (value: PreparationSummary) =
        Some(value.OperationId, value.RequestSha256)

    let private receipt (value: OperationReceipt) = Some(value.OperationId, None)

    let private prepare =
        function
        | PrepareOutcome.Prepared(details, _)
        | PrepareOutcome.RetainedForRecovery(details, _) -> summary details.Summary
        | PrepareOutcome.ObservedAccepted value -> receipt value
        | PrepareOutcome.PreparationStateUnknown(id, digest, _) -> Some(id, Some digest)
        | PrepareOutcome.PrepareRejected(id, _)
        | PrepareOutcome.PrepareFailed(id, _)
        | PrepareOutcome.CancelledBeforeAdmission id -> Some(id, None)

    let private submit =
        function
        | SubmissionOutcome.ObservedAccepted value -> receipt value
        | SubmissionOutcome.Completed(value, _, _, _)
        | SubmissionOutcome.CancelledBeforeAttempt value
        | SubmissionOutcome.AttemptAdmissionUnknown(value, _)
        | SubmissionOutcome.AttemptUnresolved(value, _, _) -> summary value
        | SubmissionOutcome.RejectedBeforeAttempt(value, _)
        | SubmissionOutcome.FailedBeforeAttempt(value, _) -> Option.bind summary value
        | SubmissionOutcome.PreparationStateUnknown(id, digest, _) -> Some(id, Some digest)
        | SubmissionOutcome.CancelledBeforeAdmission id -> Some(id, None)

    let private retain =
        function
        | RecoveryImportRetainOutcome.RetainedPreparation details
        | RecoveryImportRetainOutcome.ExistingPreparation details -> summary details.Summary
        | RecoveryImportRetainOutcome.ObservedAcceptedImport value -> receipt value
        | RecoveryImportRetainOutcome.RetainStateUnknown(_, _, id, _) ->
            Option.map (fun value -> value, None) id
        | _ -> None

    let private returned =
        function
        | EndpointReply.Prepare value -> prepare value
        | EndpointReply.Submit value -> submit value
        | EndpointReply.EnvelopeRetain value
        | EndpointReply.RecordRetain value -> retain value
        | _ -> None

    let private safeDigest (value: string option) =
        value
        |> Option.filter (fun text ->
            not (Object.ReferenceEquals(text, null))
            && text.Length = 64
            && text |> Seq.forall (fun c -> ('0' <= c && c <= '9') || ('a' <= c && c <= 'f')))

    let combine (authored: (Guid * string option) option) reply =
        let candidate =
            match authored, returned reply with
            | Some(id, digest), Some(returnedId, returnedDigest) when id = returnedId ->
                Some(id, if digest.IsSome then digest else returnedDigest)
            | Some value, _ -> Some value
            | None, value -> value

        candidate
        |> Option.filter (fun (id, _) -> id <> Guid.Empty)
        |> Option.map (fun (id, digest) -> id, safeDigest digest)
