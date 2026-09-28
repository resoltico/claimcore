namespace ClaimCore.Postgres

open System
open ClaimCore.Application

module internal CaseTombstoneTerminalPolicy =
    let private digest = CaseTombstonePruneApprovalPolicy.digest

    let private policy value =
        not (String.IsNullOrWhiteSpace(value))
        && value.Length <= 128
        && value = value.Trim()
        && (value |> Seq.forall (Char.IsControl >> not))

    let private common (value: TerminalCopyProposal) =
        value.EventId <> Guid.Empty
        && value.CaseId <> Guid.Empty
        && value.InstallationId <> Guid.Empty
        && value.LineageId <> Guid.Empty
        && value.PruneEventId <> Guid.Empty
        && value.ExpectedAuthorityRevision >= 0L
        && value.WitnessEpoch > 0L
        && value.WitnessCutoffSequence > 0L
        && value.RelevantCopyCount >= 0L
        && value.ExpectedWriterGeneration > 0L
        && (digest value.ExpectedAuthorityHash).IsSome
        && (digest value.WitnessCutoffHash).IsSome
        && (digest value.CopyInventoryDigest).IsSome
        && policy value.PolicyId

    let private finalShape =
        function
        | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ -> true
        | TombstoneTerminalProposal.CompleteSuppressionHorizon value ->
            value.OldWriterGeneration > 0L
            && value.OldWriterGeneration < Int64.MaxValue
            && value.NewWriterGeneration = value.OldWriterGeneration + 1L
            && value.Copy.ExpectedWriterGeneration = value.NewWriterGeneration
            && (digest value.RecoveryFenceDigest).IsSome

    let validProposal proposal (instant: DateTimeOffset) =
        let value = TombstoneTerminalProposal.copy proposal

        common value
        && finalShape proposal
        && instant.Offset = TimeSpan.Zero
        && value.ValidUntil.Offset = TimeSpan.Zero
        && value.SuppressionUntil.Offset = TimeSpan.Zero
        && value.ValidUntil.UtcTicks % 10L = 0L
        && value.ValidUntil > instant
        && value.ValidUntil <= instant.AddHours(24.0)

    let valid proposal approvalId (expiresAt: DateTimeOffset) (instant: DateTimeOffset) =
        let value = TombstoneTerminalProposal.copy proposal

        approvalId <> Guid.Empty
        && validProposal proposal instant
        && expiresAt.Offset = TimeSpan.Zero
        && expiresAt.UtcTicks % 10L = 0L
        && expiresAt > instant
        && expiresAt <= value.ValidUntil

    let matches (stored: StoredTerminalTombstone) proposal =
        let value = TombstoneTerminalProposal.copy proposal

        let phase =
            match proposal with
            | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ -> "ERASURE_PENDING"
            | TombstoneTerminalProposal.CompleteSuppressionHorizon _ ->
                "PAYLOAD_ERASED_SUPPRESSION_RETAINED"

        let projected =
            match proposal with
            | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ ->
                stored.CopyAbsenceEventId.IsNone && stored.SuppressionFinalEventId.IsNone
            | TombstoneTerminalProposal.CompleteSuppressionHorizon _ ->
                stored.CopyAbsenceEventId.IsSome
                && stored.SuppressionFinalEventId.IsNone
                && stored.RetentionPolicyId = Some value.PolicyId
                && stored.SuppressionUntil = Some value.SuppressionUntil

        stored.CaseId = value.CaseId
        && stored.Phase = phase
        && projected
        && stored.PruneEventId = value.PruneEventId
        && stored.CutoffSequence = value.WitnessCutoffSequence
        && stored.CutoffHash = (digest value.WitnessCutoffHash).Value
        && stored.AuthorityRevision = value.ExpectedAuthorityRevision
        && stored.AuthorityHash = (digest value.ExpectedAuthorityHash).Value
        && stored.InstallationId = value.InstallationId
        && stored.LineageId = value.LineageId
        && stored.WitnessEpoch = value.WitnessEpoch
        && stored.WriterGeneration = value.ExpectedWriterGeneration
