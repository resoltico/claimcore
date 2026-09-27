namespace ClaimCore.Contracts

open System.Globalization
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Domain

module internal WebWireLifecycle =
    let private revision (value: int64) =
        value.ToString(CultureInfo.InvariantCulture)

    let private disposition =
        function
        | CaseDisposition.Active -> "ACTIVE"
        | CaseDisposition.VoidedDataEntryError -> "VOIDED_DATA_ENTRY_ERROR"

    let privacy =
        function
        | PrivacyPhase.Active -> "ACTIVE"
        | PrivacyPhase.ErasureRequested -> "ERASURE_REQUESTED"
        | PrivacyPhase.ErasurePending -> "ERASURE_PENDING"
        | PrivacyPhase.PayloadErasedSuppressionRetained -> "PAYLOAD_ERASED_SUPPRESSION_RETAINED"
        | PrivacyPhase.ErasureFinal -> "ERASURE_FINAL"

    let private refusalValidation =
        function
        | LifecycleRefusal.InvalidIdentity -> Some "INVALID_IDENTITY"
        | LifecycleRefusal.InvalidReason -> Some "INVALID_REASON"
        | LifecycleRefusal.InvalidTime -> Some "INVALID_TIME"
        | LifecycleRefusal.WrongCase -> Some "WRONG_CASE"
        | LifecycleRefusal.VersionConflict -> Some "VERSION_CONFLICT"
        | LifecycleRefusal.RevisionExhausted -> Some "REVISION_EXHAUSTED"
        | LifecycleRefusal.WrongDisposition -> Some "WRONG_DISPOSITION"
        | LifecycleRefusal.ErasureHasBegun -> Some "ERASURE_HAS_BEGUN"
        | LifecycleRefusal.WrongPrivacyPhase -> Some "WRONG_PRIVACY_PHASE"
        | _ -> None

    let refusal reason =
        match refusalValidation reason with
        | Some token -> token
        | None ->
            match reason with
            | LifecycleRefusal.DuplicateHold -> "DUPLICATE_HOLD"
            | LifecycleRefusal.HoldNotFound -> "HOLD_NOT_FOUND"
            | LifecycleRefusal.HoldActive -> "HOLD_ACTIVE"
            | LifecycleRefusal.HoldCapacityExceeded -> "HOLD_CAPACITY_EXCEEDED"
            | LifecycleRefusal.ApprovalRequired -> "APPROVAL_REQUIRED"
            | LifecycleRefusal.ApprovalCapacityExceeded -> "APPROVAL_CAPACITY_EXCEEDED"
            | LifecycleRefusal.ApprovalMismatch -> "APPROVAL_MISMATCH"
            | LifecycleRefusal.ApprovalExpired -> "APPROVAL_EXPIRED"
            | LifecycleRefusal.ErasureEvidenceIncomplete -> "ERASURE_EVIDENCE_INCOMPLETE"
            | _ -> invalidOp "Lifecycle refusal classification is incomplete."

    let private reviewValue (writer: Utf8JsonWriter) (value: LifecycleReview) =
        writer.WriteStartObject()
        writer.WriteString("businessRevision", revision value.BusinessRevision)
        writer.WriteString("lifecycleSequence", revision value.LifecycleSequence)
        writer.WriteString("lifecycleHash", value.LifecycleHash)
        writer.WriteString("disposition", disposition value.Disposition)
        writer.WriteString("privacyPhase", privacy value.PrivacyPhase)
        writer.WritePropertyName("activeHolds")
        writer.WriteStartArray()

        for hold in value.ActiveHolds do
            writer.WriteStartObject()
            writer.WriteString("holdId", hold.HoldId)
            writer.WriteString("reviewOn", hold.ReviewOn.ToString("yyyy-MM-dd"))
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteBoolean("voidRequiresTwoApprovals", value.VoidRequiresTwoApprovals)
        writer.WriteEndObject()

    let review (writer: Utf8JsonWriter) =
        function
        | LifecycleReviewOutcome.Available value ->
            WebWireQueries.outcome writer "AVAILABLE" (fun () -> reviewValue writer value)
        | LifecycleReviewOutcome.ResourceUnavailable ->
            WebWireQueries.outcome writer "RESOURCE_UNAVAILABLE" writer.WriteNullValue
        | LifecycleReviewOutcome.Cancelled ->
            WebWireQueries.outcome writer "CANCELLED" writer.WriteNullValue
        | LifecycleReviewOutcome.Failed fault ->
            WebWireQueries.outcome writer "FAILED" (fun () -> CliWireValues.fault writer fault)

    let write (writer: Utf8JsonWriter) =
        function
        | LifecycleWriteOutcome.Applied(eventId, businessRevision, lifecycleSequence) ->
            WebWireQueries.outcome writer "APPLIED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("eventId", eventId)
                writer.WriteString("businessRevision", revision businessRevision)
                writer.WriteString("lifecycleSequence", revision lifecycleSequence)
                writer.WriteEndObject())
        | LifecycleWriteOutcome.Refused reason ->
            WebWireQueries.outcome writer "REFUSED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("reason", refusal reason)
                writer.WriteEndObject())
        | LifecycleWriteOutcome.ResourceUnavailable ->
            WebWireQueries.outcome writer "RESOURCE_UNAVAILABLE" writer.WriteNullValue
        | LifecycleWriteOutcome.CancelledBeforeAdmission eventId ->
            WebWireQueries.outcome writer "CANCELLED_BEFORE_ADMISSION" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("eventId", eventId)
                writer.WriteEndObject())
        | LifecycleWriteOutcome.Failed fault ->
            WebWireQueries.outcome writer "FAILED" (fun () -> CliWireValues.fault writer fault)
        | LifecycleWriteOutcome.Unconfirmed eventId ->
            WebWireQueries.outcome writer "UNCONFIRMED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("eventId", eventId)
                writer.WriteEndObject())
