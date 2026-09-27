namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application

[<NoEquality; NoComparison>]
type internal ErasurePurgeApprovalReceipt =
    {
        ApprovalId: Guid
        ApproverId: Guid
        GrantRevision: int64
        ApprovedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        DraftSha256: byte array
        DraftCommitment: byte array
        Canonical: byte array
        CandidateSha256: byte array
        ApprovalCommitment: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
    }

[<NoEquality; NoComparison>]
type internal PreparedErasurePurge =
    {
        RequestEventId: Guid
        RequestCommitment: byte array
        ProposalCommitment: byte array
        Approvals: ErasurePurgeApprovalReceipt list
        CopySeal: ManagedCopyInventorySeal
    }

module internal CaseErasurePurgeCandidate =
    let private writeApproval (writer: Utf8JsonWriter) (approval: ErasurePurgeApprovalReceipt) =
        writer.WriteStartObject()
        writer.WriteString("approvalId", approval.ApprovalId)
        writer.WriteString("approverActorId", approval.ApproverId)
        writer.WriteNumber("approverGrantRevision", approval.GrantRevision)
        writer.WriteString("approvedAt", approval.ApprovedAt.ToString("O"))
        writer.WriteString("expiresAt", approval.ExpiresAt.ToString("O"))
        writer.WriteBase64String("draftCommitment", ReadOnlySpan<byte>(approval.DraftCommitment))

        writer.WriteBase64String(
            "approvalCommitment",
            ReadOnlySpan<byte>(approval.ApprovalCommitment)
        )

        writer.WriteNumber("witnessSequence", approval.WitnessSequence)
        writer.WriteNumber("witnessEpoch", approval.WitnessEpoch)
        writer.WriteBase64String("witnessHash", ReadOnlySpan<byte>(approval.WitnessHash))
        writer.WriteEndObject()

    let private writeSeal
        (writer: Utf8JsonWriter)
        (witnessSeal: WitnessDenialSeal)
        (copySeal: ManagedCopyInventorySeal)
        (at: DateTimeOffset)
        =
        writer.WriteNumber("witnessCutoffSequence", witnessSeal.CutoffSequence)
        writer.WriteBase64String("witnessCutoffHash", ReadOnlySpan<byte>(witnessSeal.CutoffHash))
        writer.WriteNumber("subjectIntentCount", witnessSeal.IntentCount)

        writer.WriteBase64String(
            "subjectIntentSha256",
            ReadOnlySpan<byte>(witnessSeal.IntentDigest)
        )

        writer.WriteNumber("denialCount", witnessSeal.DenialCount)
        writer.WriteBase64String("denialSetSha256", ReadOnlySpan<byte>(witnessSeal.DenialDigest))
        writer.WriteNumber("managedCopyCount", copySeal.CopyCount)

        writer.WriteBase64String(
            "copyInventorySha256",
            ReadOnlySpan<byte>(copySeal.InventorySha256)
        )

        writer.WriteString("observedUtcInstant", at.ToString("O"))

    let private writeTail
        (writer: Utf8JsonWriter)
        (change: LifecycleChange)
        (approvals: ErasurePurgeApprovalReceipt list)
        =
        match change.Action with
        | LifecycleMutation.PurgeLivePayload(_, validUntil) ->
            writer.WriteString("validUntil", validUntil.ToString("O"))
        | _ -> invalidArg (nameof change) "Only a live purge proposal is canonical here."

        writer.WriteStartArray("approvalReceipts")
        approvals |> List.sortBy _.ApprovalId |> List.iter (writeApproval writer)
        writer.WriteEndArray()

    let private writeProof
        (writer: Utf8JsonWriter)
        (change: LifecycleChange)
        (caseId: Guid)
        (keyId: Guid)
        (referenceCommitment: byte array)
        (sourceRevision: int64)
        (lifecycleSequence: int64)
        (lifecycleHash: byte array)
        (requestEventId: Guid)
        (requestDenialCount: int64)
        (requestDenialDigest: byte array)
        (requestCandidateCommitment: byte array)
        (proposalCommitment: byte array)
        (witnessSeal: WitnessDenialSeal)
        (copySeal: ManagedCopyInventorySeal)
        (at: DateTimeOffset)
        =
        writer.WriteNumber("version", 1)
        writer.WriteString("kind", "CASE_ERASURE_LIVE_PURGE")
        writer.WriteString("executorKind", "SCHEMA_OWNER_PROCESS")
        writer.WriteString("eventId", change.EventId)
        writer.WriteString("caseId", caseId)
        writer.WriteString("suppressionKeyId", keyId)
        writer.WriteBase64String("referenceCommitment", ReadOnlySpan<byte>(referenceCommitment))
        writer.WriteNumber("sourceRevision", sourceRevision)
        writer.WriteNumber("lifecycleSequence", lifecycleSequence)
        writer.WriteBase64String("lifecycleHash", ReadOnlySpan<byte>(lifecycleHash))
        writer.WriteString("requestEventId", requestEventId)
        writer.WriteNumber("requestDenialCount", requestDenialCount)
        writer.WriteBase64String("requestDenialSetSha256", ReadOnlySpan<byte>(requestDenialDigest))

        writer.WriteBase64String(
            "requestCandidateCommitment",
            ReadOnlySpan<byte>(requestCandidateCommitment)
        )

        writer.WriteBase64String("proposalCommitment", ReadOnlySpan<byte>(proposalCommitment))
        writeSeal writer witnessSeal copySeal at

    let encode
        (change: LifecycleChange)
        (caseId: Guid)
        (keyId: Guid)
        (referenceCommitment: byte array)
        (sourceRevision: int64)
        (lifecycleSequence: int64)
        (lifecycleHash: byte array)
        (requestEventId: Guid)
        (requestDenialCount: int64)
        (requestDenialDigest: byte array)
        (requestCandidateCommitment: byte array)
        (proposalCommitment: byte array)
        (witnessSeal: WitnessDenialSeal)
        (copySeal: ManagedCopyInventorySeal)
        (approvals: ErasurePurgeApprovalReceipt list)
        (at: DateTimeOffset)
        =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()

        writeProof
            writer
            change
            caseId
            keyId
            referenceCommitment
            sourceRevision
            lifecycleSequence
            lifecycleHash
            requestEventId
            requestDenialCount
            requestDenialDigest
            requestCandidateCommitment
            proposalCommitment
            witnessSeal
            copySeal
            at

        writeTail writer change approvals
        writer.WriteEndObject()
        writer.Flush()
        let bytes = buffer.ToArray()

        if bytes.Length > 16384 then
            invalidOp "Erasure purge candidate is too large."

        CryptographicOperations.ZeroMemory(buffer.GetBuffer().AsSpan(0, int buffer.Length))
        bytes
