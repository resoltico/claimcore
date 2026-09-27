namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application

/// Distinct nonclaimant authority bytes for the two owner-terminal draft approvals.
module internal CaseTombstoneTerminalCandidate =
    let private encode (write: Utf8JsonWriter -> unit) =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)
        write writer
        writer.Flush()
        buffer.ToArray()

    let private writeCommon (writer: Utf8JsonWriter) (value: TerminalCopyProposal) =
        writer.WriteString("eventId", value.EventId)
        writer.WriteString("caseId", value.CaseId)
        writer.WriteNumber("expectedAuthorityRevision", value.ExpectedAuthorityRevision)
        writer.WriteString("expectedAuthorityHash", value.ExpectedAuthorityHash)
        writer.WriteString("installationId", value.InstallationId)
        writer.WriteString("lineageId", value.LineageId)
        writer.WriteNumber("witnessEpoch", value.WitnessEpoch)
        writer.WriteString("pruneEventId", value.PruneEventId)
        writer.WriteNumber("witnessCutoffSequence", value.WitnessCutoffSequence)
        writer.WriteString("witnessCutoffHash", value.WitnessCutoffHash)
        writer.WriteString("copyInventoryDigest", value.CopyInventoryDigest)
        writer.WriteNumber("relevantCopyCount", value.RelevantCopyCount)
        writer.WriteNumber("expectedWriterGeneration", value.ExpectedWriterGeneration)
        writer.WriteString("policyId", value.PolicyId)
        writer.WriteString("suppressionUntil", value.SuppressionUntil.ToString("O"))
        writer.WriteString("validUntil", value.ValidUntil.ToString("O"))

    let proposal (value: TombstoneTerminalProposal) =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("version", 1)

            match value with
            | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence copy ->
                writer.WriteString("kind", "CONFIRM_MANAGED_PAYLOAD_ABSENCE")
                writeCommon writer copy
            | TombstoneTerminalProposal.CompleteSuppressionHorizon final ->
                writer.WriteString("kind", "COMPLETE_SUPPRESSION_HORIZON")
                writeCommon writer final.Copy
                writer.WriteString("recoveryFenceDigest", final.RecoveryFenceDigest)
                writer.WriteNumber("oldWriterGeneration", final.OldWriterGeneration)
                writer.WriteNumber("newWriterGeneration", final.NewWriterGeneration)

            writer.WriteEndObject())

    let approval
        (value: TombstoneTerminalProposal)
        (approvalId: Guid)
        (actorId: Guid)
        (grantRevision: int64)
        (approvedAt: DateTimeOffset)
        (expiresAt: DateTimeOffset)
        =
        let proposalBytes = proposal value

        try
            encode (fun writer ->
                writer.WriteStartObject()
                writer.WriteNumber("version", 1)
                writer.WriteString("kind", "CASE_TERMINAL_ERASURE_APPROVAL")
                writer.WriteString("approvalId", approvalId)
                writer.WriteString("caseId", TombstoneTerminalProposal.caseId value)
                writer.WriteString("terminalEventId", TombstoneTerminalProposal.eventId value)
                writer.WriteBase64String("proposal", ReadOnlySpan<byte>(proposalBytes))
                writer.WriteString("approverActorId", actorId)
                writer.WriteNumber("approverGrantRevision", grantRevision)
                writer.WriteString("approvedAt", approvedAt.ToString("O"))
                writer.WriteString("expiresAt", expiresAt.ToString("O"))
                writer.WriteEndObject())
        finally
            CryptographicOperations.ZeroMemory(proposalBytes)
