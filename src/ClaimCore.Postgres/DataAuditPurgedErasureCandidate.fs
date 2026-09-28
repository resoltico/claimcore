namespace ClaimCore.Postgres

open System
open System.Globalization
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application

/// Reconstructs the exact retained nonpayload canonical purge event from SQL projections and
/// two minimal approvals. The raw claimant reference and reason cannot be reconstructed here.
module internal DataAuditPurgedErasureCandidate =
    let private invalid () : 'a =
        invalidOp "Purged case candidate differs."

    let private observed (value: PurgedErasureAuditRow) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(value.Canonical))
        let root = document.RootElement

        let observedAt =
            DateTimeOffset.ParseExact(
                root.GetProperty("observedUtcInstant").GetString()
                |> Option.ofObj
                |> Option.defaultWith invalid,
                "O",
                CultureInfo.InvariantCulture
            )

        let copyCount = root.GetProperty("managedCopyCount").GetInt64()

        if
            copyCount < 0L
            || observedAt <> value.LivePurgedAt
            || value.ExecutorKind <> "SCHEMA_OWNER_PROCESS"
            || not (
                [ "ERASURE_PENDING"; "PAYLOAD_ERASED_SUPPRESSION_RETAINED"; "ERASURE_FINAL" ]
                |> List.contains value.Phase
            )
            || value.PurgeRevision < value.RequestRevision
            || value.PurgeLifecycleSequence < value.RequestLifecycleSequence
            || value.ValidUntil <= value.LivePurgedAt
            || value.RequestCandidateCommitment.Length <> 32
            || value.ProposalCommitment.Length <> 32
            || value.CopyInventoryDigest.Length <> 32
            || value.ReferenceCommitment.Length <> 32
        then
            invalid ()

        observedAt, copyCount

    let private reconstruct (value: PurgedErasureAuditRow) approvals observedAt copyCount =

        let change: LifecycleChange =
            {
                EventId = value.PurgeEventId
                CaseReference = ""
                ExpectedRevision = value.PurgeRevision
                ExpectedLifecycleSequence = value.PurgeLifecycleSequence
                ExpectedLifecycleHash = Convert.ToHexStringLower value.PurgeLifecycleHash
                Action = LifecycleMutation.PurgeLivePayload("", value.ValidUntil)
            }

        let witnessSeal: WitnessDenialSeal =
            {
                CutoffSequence = value.CutoffSequence
                CutoffHash = value.CutoffHash
                IntentCount = value.SubjectIntentCount
                IntentDigest = value.SubjectIntentDigest
                DenialCount = value.DenialCount
                DenialDigest = value.DenialDigest
            }

        let copySeal: ManagedCopyInventorySeal =
            {
                CaseId = value.CaseId
                WitnessCutoffSequence = value.CutoffSequence
                WitnessCutoffHash = value.CutoffHash
                InventorySha256 = value.CopyInventoryDigest
                CopyCount = copyCount
                ObservedAt = observedAt
            }

        CaseErasurePurgeCandidate.encode
            change
            value.CaseId
            value.SuppressionKeyId
            value.ReferenceCommitment
            value.PurgeRevision
            value.PurgeLifecycleSequence
            value.PurgeLifecycleHash
            value.RequestEventId
            value.RequestDenialCount
            value.RequestDenialDigest
            value.RequestCandidateCommitment
            value.ProposalCommitment
            witnessSeal
            copySeal
            approvals
            observedAt

    let verify (value: PurgedErasureAuditRow) approvals =
        let observedAt, copyCount = observed value
        let expected = reconstruct value approvals observedAt copyCount

        try
            if value.Canonical <> expected || value.CandidateHash <> SHA256.HashData(expected) then
                invalid ()
        finally
            CryptographicOperations.ZeroMemory(expected)
