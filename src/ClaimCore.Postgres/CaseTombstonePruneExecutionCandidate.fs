namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application

[<NoEquality; NoComparison>]
type internal PruneApprovalReceipt =
    {
        ApprovalId: Guid
        ActorId: Guid
        GrantRevision: int64
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
    }

/// This candidate retains only pseudonymous technical evidence, never claimant fields, case
/// reference, reason, or a claim that managed copies have been deleted.
module internal CaseTombstonePruneExecutionCandidate =
    let private writeApproval (writer: Utf8JsonWriter) (value: PruneApprovalReceipt) =
        writer.WriteStartObject()
        writer.WriteString("approvalId", value.ApprovalId)
        writer.WriteString("actorId", value.ActorId)
        writer.WriteNumber("grantRevision", value.GrantRevision)
        writer.WriteBase64String("candidateHash", ReadOnlySpan<byte>(value.CandidateHash))
        writer.WriteNumber("witnessSequence", value.WitnessSequence)
        writer.WriteNumber("witnessEpoch", value.WitnessEpoch)
        writer.WriteBase64String("witnessHash", ReadOnlySpan<byte>(value.WitnessHash))
        writer.WriteEndObject()

    let encode
        (proposal: TombstonePruneProposal)
        (copyInventoryDigest: byte array)
        (approvals: PruneApprovalReceipt list)
        =
        if copyInventoryDigest.Length <> 32 || approvals.Length <> 2 then
            invalidArg (nameof approvals) "Witness prune authority evidence is incomplete."

        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("version", 1)
        writer.WriteString("kind", "CASE_WITNESS_PRUNE_EXECUTION")
        writer.WriteString("executorKind", "SCHEMA_OWNER_PROCESS")
        writer.WriteString("eventId", proposal.EventId)
        writer.WriteString("caseId", proposal.CaseId)
        writer.WriteString("purgeEventId", proposal.PurgeEventId)
        writer.WriteNumber("purgeWitnessSequence", proposal.PurgeWitnessSequence)
        writer.WriteNumber("purgeWitnessEpoch", proposal.PurgeWitnessEpoch)
        writer.WriteString("purgeWitnessHash", proposal.PurgeWitnessHash)
        writer.WriteNumber("cutoffSequence", proposal.CutoffSequence)
        writer.WriteString("cutoffHash", proposal.CutoffHash)
        writer.WriteNumber("targetCount", proposal.TargetCount)
        writer.WriteString("targetDigest", proposal.TargetDigest)
        writer.WriteBase64String("copyInventorySha256", ReadOnlySpan<byte>(copyInventoryDigest))
        writer.WriteNumber("authorityRevision", proposal.ExpectedAuthorityRevision)
        writer.WriteString("authorityHash", proposal.ExpectedAuthorityHash)
        writer.WriteString("validUntil", proposal.ValidUntil.ToString("O"))
        writer.WriteStartArray("approvalReceipts")
        approvals |> List.sortBy _.ApprovalId |> List.iter (writeApproval writer)
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
        let canonical = buffer.ToArray()
        CryptographicOperations.ZeroMemory(buffer.GetBuffer().AsSpan(0, int buffer.Length))
        canonical
