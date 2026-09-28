namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application
open OwnerTerminalDecisionData

/// Nonclaimant owner-technical terminal event. The two steward approvals and signed proof
/// digests are bound to exact canonical bytes before witness INTENT and primary co-commit.
module internal CaseTombstoneTerminalEventCandidate =
    let encodeProof
        proposal
        (copyProofSha256: byte array)
        (fenceProofSha256: byte array option)
        (approvalOneId: Guid)
        (approvalTwoId: Guid)
        (actorAuthorityRevision: int64)
        (previousRevision: int64)
        (previousHash: byte array)
        (observedAt: DateTimeOffset)
        =
        let proposalBytes = CaseTombstoneTerminalCandidate.proposal proposal

        try
            use buffer = new MemoryStream()
            use writer = new Utf8JsonWriter(buffer)
            let common = TombstoneTerminalProposal.copy proposal
            writer.WriteStartObject()
            writer.WriteNumber("version", 1)
            writer.WriteString("kind", "CASE_TERMINAL_OWNER_EVENT")
            writer.WriteString("eventId", common.EventId)
            writer.WriteString("caseId", common.CaseId)
            writer.WriteString("executorKind", "SCHEMA_OWNER_PROCESS")
            writer.WriteBase64String("proposal", ReadOnlySpan<byte>(proposalBytes))
            writer.WriteBase64String("copyProofSha256", ReadOnlySpan<byte>(copyProofSha256))

            match fenceProofSha256 with
            | None -> writer.WriteNull("recoveryFenceProofSha256")
            | Some value ->
                writer.WriteBase64String("recoveryFenceProofSha256", ReadOnlySpan<byte>(value))

            writer.WriteString("approvalOneId", approvalOneId)
            writer.WriteString("approvalTwoId", approvalTwoId)
            writer.WriteNumber("actorAuthorityRevision", actorAuthorityRevision)
            writer.WriteNumber("authorityRevision", previousRevision + 1L)
            writer.WriteBase64String("previousAuthorityHash", ReadOnlySpan<byte>(previousHash))
            writer.WriteString("observedUtcInstant", observedAt.ToString("O"))
            writer.WriteEndObject()
            writer.Flush()
            buffer.ToArray()
        finally
            CryptographicOperations.ZeroMemory(proposalBytes)

    let encode
        proposal
        (copy: OwnerCopyAbsenceCertificate)
        (fence: OwnerRecoveryFenceCertificate option)
        (approvals: ApprovalEvidence list)
        actorAuthorityRevision
        previousRevision
        previousHash
        observedAt
        =
        encodeProof
            proposal
            copy.SignedProofSha256
            (fence |> Option.map _.SignedProofSha256)
            approvals[0].ApprovalId
            approvals[1].ApprovalId
            actorAuthorityRevision
            previousRevision
            previousHash
            observedAt

    let digest (canonical: byte array) = SHA256.HashData(canonical)

    let eventHash (previousHash: byte array) (canonical: byte array) =
        CaseTombstoneCandidate.eventHash previousHash canonical
