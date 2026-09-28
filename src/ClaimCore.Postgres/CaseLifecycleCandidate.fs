namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Domain

/// Byte-stable lifecycle candidates are the same bytes committed in primary history and
/// encrypted in the independent witness. They are deliberately distinct from business commands.
module internal CaseLifecycleCandidate =
    let actionName =
        function
        | LifecycleMutation.VoidDataEntryError _ -> "VOID_DATA_ENTRY_ERROR"
        | LifecycleMutation.ReinstateVoided _ -> "REINSTATE_VOIDED"
        | LifecycleMutation.RequestErasure _ -> "REQUEST_ERASURE"
        | LifecycleMutation.MarkErasurePending _ -> "MARK_ERASURE_PENDING"
        | LifecycleMutation.PurgeLivePayload _ -> "PURGE_PAYLOAD"
        | LifecycleMutation.RecordHold _ -> "RECORD_HOLD"
        | LifecycleMutation.ReleaseHold _ -> "RELEASE_HOLD"

    let dispositionName =
        function
        | CaseDisposition.Active -> "ACTIVE"
        | CaseDisposition.VoidedDataEntryError -> "VOIDED_DATA_ENTRY_ERROR"

    let privacyName =
        function
        | PrivacyPhase.Active -> "ACTIVE"
        | PrivacyPhase.ErasureRequested -> "ERASURE_REQUESTED"
        | PrivacyPhase.ErasurePending -> "ERASURE_PENDING"
        | PrivacyPhase.PayloadErasedSuppressionRetained -> "PAYLOAD_ERASED_SUPPRESSION_RETAINED"
        | PrivacyPhase.ErasureFinal -> "ERASURE_FINAL"

    let parseDisposition =
        function
        | "ACTIVE" -> Some CaseDisposition.Active
        | "VOIDED_DATA_ENTRY_ERROR" -> Some CaseDisposition.VoidedDataEntryError
        | _ -> None

    let parsePrivacy =
        function
        | "ACTIVE" -> Some PrivacyPhase.Active
        | "ERASURE_REQUESTED" -> Some PrivacyPhase.ErasureRequested
        | "ERASURE_PENDING" -> Some PrivacyPhase.ErasurePending
        | "PAYLOAD_ERASED_SUPPRESSION_RETAINED" ->
            Some PrivacyPhase.PayloadErasedSuppressionRetained
        | "ERASURE_FINAL" -> Some PrivacyPhase.ErasureFinal
        | _ -> None

    let private encode write =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        write writer
        writer.Flush()
        let bytes = stream.ToArray()
        CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, int stream.Length))
        bytes

    let private writeMutation (writer: Utf8JsonWriter) mutation =
        writer.WriteStartObject("action")
        writer.WriteString("name", actionName mutation)

        match mutation with
        | LifecycleMutation.VoidDataEntryError reason
        | LifecycleMutation.ReinstateVoided reason
        | LifecycleMutation.RequestErasure reason -> writer.WriteString("reason", reason)
        | LifecycleMutation.MarkErasurePending reason -> writer.WriteString("reason", reason)
        | LifecycleMutation.PurgeLivePayload(reason, validUntil) ->
            writer.WriteString("reason", reason)
            writer.WriteString("validUntil", validUntil.ToString("O"))
        | LifecycleMutation.RecordHold(holdId, ground, reviewOn) ->
            writer.WriteString("holdId", holdId)
            writer.WriteString("ground", ground)
            writer.WriteString("reviewOn", reviewOn.ToString("yyyy-MM-dd"))
        | LifecycleMutation.ReleaseHold(holdId, reason) ->
            writer.WriteString("holdId", holdId)
            writer.WriteString("reason", reason)

        writer.WriteEndObject()

    let draft (caseId: Guid) (change: LifecycleChange) =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("version", 1)
            writer.WriteString("kind", "CASE_LIFECYCLE_DRAFT")
            writer.WriteString("eventId", change.EventId)
            writer.WriteString("caseId", caseId)
            writer.WriteString("caseReference", change.CaseReference)
            writer.WriteNumber("expectedRevision", change.ExpectedRevision)
            writer.WriteNumber("expectedLifecycleSequence", change.ExpectedLifecycleSequence)
            writer.WriteString("expectedLifecycleHash", change.ExpectedLifecycleHash)
            writeMutation writer change.Action
            writer.WriteEndObject())

    let event
        (draftBytes: byte array)
        (businessRevision: int64)
        (lifecycleSequence: int64)
        (previousHash: byte array)
        (disposition: CaseDisposition)
        (privacy: PrivacyPhase)
        (instant: DateTimeOffset)
        (actorId: Guid)
        (grantRevision: int64)
        (approvalIds: Guid list)
        (snapshot: byte array option)
        =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("version", 1)
            writer.WriteString("kind", "CASE_LIFECYCLE_EVENT")
            writer.WriteBase64String("draft", ReadOnlySpan<byte>(draftBytes))
            writer.WriteNumber("businessRevision", businessRevision)
            writer.WriteNumber("lifecycleSequence", lifecycleSequence)
            writer.WriteBase64String("previousHash", ReadOnlySpan<byte>(previousHash))
            writer.WriteString("disposition", dispositionName disposition)
            writer.WriteString("privacyPhase", privacyName privacy)
            writer.WriteString("observedUtcInstant", instant.ToString("O"))
            writer.WriteString("actorId", actorId)
            writer.WriteNumber("grantRevision", grantRevision)
            writer.WriteStartArray("approvalIds")
            approvalIds |> List.sort |> List.iter writer.WriteStringValue
            writer.WriteEndArray()

            match snapshot with
            | Some value -> writer.WriteBase64String("snapshot", ReadOnlySpan<byte>(value))
            | None -> writer.WriteNull("snapshot")

            writer.WriteEndObject())

    let approval
        (approvalId: Guid)
        (operationId: Guid)
        (caseId: Guid)
        (draftDigest: byte array)
        (approverId: Guid)
        (grantRevision: int64)
        (approvedAt: DateTimeOffset)
        (expiresAt: DateTimeOffset)
        =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("version", 1)
            writer.WriteString("kind", "CASE_LIFECYCLE_APPROVAL")
            writer.WriteString("approvalId", approvalId)
            writer.WriteString("operationId", operationId)
            writer.WriteString("caseId", caseId)
            writer.WriteBase64String("draftDigest", ReadOnlySpan<byte>(draftDigest))
            writer.WriteString("approverActorId", approverId)
            writer.WriteNumber("grantRevision", grantRevision)
            writer.WriteString("approvedAt", approvedAt.ToString("O"))
            writer.WriteString("expiresAt", expiresAt.ToString("O"))
            writer.WriteEndObject())

    let eventHash (previousHash: byte array) (candidateDigest: byte array) =
        let concatenated = Array.append previousHash candidateDigest

        try
            SHA256.HashData(concatenated)
        finally
            CryptographicOperations.ZeroMemory(concatenated)
