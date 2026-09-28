namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application

/// Canonical nonclaimant authority bytes, independently hash-bound to the witness journal.
module internal CaseTombstoneCandidate =
    let private encode (write: Utf8JsonWriter -> unit) =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)
        write writer
        writer.Flush()
        buffer.ToArray()

    let hold
        (change: TombstoneHoldChange)
        (actorId: Guid)
        (grantRevision: int64)
        (previousHash: byte array)
        (instant: DateTimeOffset)
        =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("version", 1)

            match change.Mutation with
            | TombstoneHoldMutation.Record(holdId, ground, reviewOn) ->
                writer.WriteString("kind", "CASE_TOMBSTONE_HOLD_RECORD")
                writer.WriteString("holdId", holdId)
                writer.WriteString("groundCode", ground)
                writer.WriteString("reviewOn", reviewOn.ToString("yyyy-MM-dd"))
            | TombstoneHoldMutation.Release(holdId, releaseCode) ->
                writer.WriteString("kind", "CASE_TOMBSTONE_HOLD_RELEASE")
                writer.WriteString("holdId", holdId)
                writer.WriteString("releaseCode", releaseCode)

            writer.WriteString("eventId", change.EventId)
            writer.WriteString("caseId", change.CaseId)
            writer.WriteNumber("revision", change.ExpectedAuthorityRevision + 1L)
            writer.WriteBase64String("previousHash", ReadOnlySpan<byte>(previousHash))
            writer.WriteString("actorId", actorId)
            writer.WriteNumber("grantRevision", grantRevision)
            writer.WriteString("observedUtcInstant", instant.ToString("O"))
            writer.WriteEndObject())

    let proposal (value: TombstonePruneProposal) =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("version", 1)
            writer.WriteString("kind", "CASE_WITNESS_PRUNE_PROPOSAL")
            writer.WriteString("eventId", value.EventId)
            writer.WriteString("caseId", value.CaseId)
            writer.WriteString("purgeEventId", value.PurgeEventId)
            writer.WriteNumber("purgeWitnessSequence", value.PurgeWitnessSequence)
            writer.WriteNumber("purgeWitnessEpoch", value.PurgeWitnessEpoch)
            writer.WriteString("purgeWitnessHash", value.PurgeWitnessHash)
            writer.WriteNumber("cutoffSequence", value.CutoffSequence)
            writer.WriteString("cutoffHash", value.CutoffHash)
            writer.WriteNumber("targetCount", value.TargetCount)
            writer.WriteString("targetDigest", value.TargetDigest)
            writer.WriteNumber("expectedAuthorityRevision", value.ExpectedAuthorityRevision)
            writer.WriteString("expectedAuthorityHash", value.ExpectedAuthorityHash)
            writer.WriteString("validUntil", value.ValidUntil.ToString("O"))
            writer.WriteEndObject())

    let approval
        (value: TombstonePruneProposal)
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
                writer.WriteString("kind", "CASE_WITNESS_PRUNE_APPROVAL")
                writer.WriteString("approvalId", approvalId)
                writer.WriteString("caseId", value.CaseId)
                writer.WriteString("pruneEventId", value.EventId)
                writer.WriteBase64String("proposal", ReadOnlySpan<byte>(proposalBytes))
                writer.WriteString("approverActorId", actorId)
                writer.WriteNumber("approverGrantRevision", grantRevision)
                writer.WriteString("approvedAt", approvedAt.ToString("O"))
                writer.WriteString("expiresAt", expiresAt.ToString("O"))
                writer.WriteEndObject())
        finally
            CryptographicOperations.ZeroMemory(proposalBytes)

    let eventHash (previousHash: byte array) (candidate: byte array) =
        let combined = Array.append previousHash (SHA256.HashData(candidate))

        try
            SHA256.HashData(combined)
        finally
            CryptographicOperations.ZeroMemory(combined)
