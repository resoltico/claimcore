namespace ClaimCore.Contracts

open System.Globalization
open System.Text.Json
open ClaimCore.Application

module internal WebWireTombstone =
    let private revision (value: int64) =
        value.ToString(CultureInfo.InvariantCulture)

    let private reviewValue (writer: Utf8JsonWriter) (value: TombstoneReview) =
        writer.WriteStartObject()
        writer.WriteString("caseId", value.CaseId)
        writer.WriteString("purgeEventId", value.PurgeEventId)
        writer.WriteString("purgeWitnessSequence", revision value.PurgeWitnessSequence)
        writer.WriteString("purgeWitnessEpoch", revision value.PurgeWitnessEpoch)
        writer.WriteString("purgeWitnessHash", value.PurgeWitnessHash)
        writer.WriteString("cutoffSequence", revision value.CutoffSequence)
        writer.WriteString("cutoffHash", value.CutoffHash)
        writer.WriteString("targetCount", revision value.TargetCount)
        writer.WriteString("targetDigest", value.TargetDigest)
        writer.WriteString("privacyPhase", WebWireLifecycle.privacy value.PrivacyPhase)
        writer.WriteBoolean("witnessPayloadPruned", value.WitnessPayloadPruned)

        writer.WriteBoolean(
            "managedCopyCertificationPending",
            value.ManagedCopyCertificationPending
        )

        writer.WriteString("authorityRevision", revision value.AuthorityRevision)
        writer.WriteString("authorityHash", value.AuthorityHash)
        writer.WritePropertyName("activeHolds")
        writer.WriteStartArray()

        for hold in value.ActiveHolds do
            writer.WriteStartObject()
            writer.WriteString("holdId", hold.HoldId)
            writer.WriteString("reviewOn", hold.ReviewOn.ToString("yyyy-MM-dd"))
            writer.WriteEndObject()

        writer.WriteEndArray()

        writer.WriteNumber(
            "requiredDistinctStewardApprovals",
            value.RequiredDistinctStewardApprovals
        )

        writer.WriteEndObject()

    let review (writer: Utf8JsonWriter) =
        function
        | TombstoneReviewOutcome.Available value ->
            WebWireQueries.outcome writer "AVAILABLE" (fun () -> reviewValue writer value)
        | TombstoneReviewOutcome.ResourceUnavailable ->
            WebWireQueries.outcome writer "RESOURCE_UNAVAILABLE" writer.WriteNullValue
        | TombstoneReviewOutcome.Cancelled ->
            WebWireQueries.outcome writer "CANCELLED" writer.WriteNullValue
        | TombstoneReviewOutcome.Failed fault ->
            WebWireQueries.outcome writer "FAILED" (fun () -> CliWireValues.fault writer fault)

    let write (writer: Utf8JsonWriter) =
        function
        | TombstoneWriteOutcome.Applied(eventId, authorityRevision) ->
            WebWireQueries.outcome writer "APPLIED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("eventId", eventId)
                writer.WriteString("authorityRevision", revision authorityRevision)
                writer.WriteEndObject())
        | TombstoneWriteOutcome.Refused reason ->
            WebWireQueries.outcome writer "REFUSED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("reason", WebWireLifecycle.refusal reason)
                writer.WriteEndObject())
        | TombstoneWriteOutcome.ResourceUnavailable ->
            WebWireQueries.outcome writer "RESOURCE_UNAVAILABLE" writer.WriteNullValue
        | TombstoneWriteOutcome.CancelledBeforeAdmission eventId ->
            WebWireQueries.outcome writer "CANCELLED_BEFORE_ADMISSION" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("eventId", eventId)
                writer.WriteEndObject())
        | TombstoneWriteOutcome.Failed fault ->
            WebWireQueries.outcome writer "FAILED" (fun () -> CliWireValues.fault writer fault)
        | TombstoneWriteOutcome.Unconfirmed eventId ->
            WebWireQueries.outcome writer "UNCONFIRMED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("eventId", eventId)
                writer.WriteEndObject())
