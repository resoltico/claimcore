namespace ClaimCore.Postgres

open System
open System.Threading.Tasks
open System.IO
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Domain

module internal CaseTombstonePruneOwnerChecks =
    let private invalid () =
        raise (InvalidDataException("Witness prune owner proof is inconsistent."))

    let digest text =
        CaseTombstonePruneApprovalPolicy.digest text |> Option.defaultWith invalid

    let validProposal (value: TombstonePruneProposal) =
        value.EventId <> Guid.Empty
        && value.CaseId <> Guid.Empty
        && value.PurgeEventId <> Guid.Empty
        && value.PurgeWitnessSequence > 0L
        && value.PurgeWitnessEpoch > 0L
        && value.CutoffSequence >= value.PurgeWitnessSequence
        && value.TargetCount > 0L
        && value.ExpectedAuthorityRevision >= 0L
        && value.ValidUntil.Offset = TimeSpan.Zero
        && value.ValidUntil.UtcTicks % 10L = 0L
        && (CaseTombstonePruneApprovalPolicy.digest value.PurgeWitnessHash).IsSome
        && (CaseTombstonePruneApprovalPolicy.digest value.CutoffHash).IsSome
        && (CaseTombstonePruneApprovalPolicy.digest value.TargetDigest).IsSome
        && (CaseTombstonePruneApprovalPolicy.digest value.ExpectedAuthorityHash).IsSome

    let matchesTombstone (value: TombstonePruneProposal) (stored: StoredCaseTombstone) =
        stored.CaseId = value.CaseId
        && stored.PurgeEventId = value.PurgeEventId
        && stored.PurgeWitnessSequence = value.PurgeWitnessSequence
        && stored.PurgeWitnessEpoch = value.PurgeWitnessEpoch
        && stored.PurgeWitnessHash = digest value.PurgeWitnessHash
        && stored.AuthorityRevision = value.ExpectedAuthorityRevision
        && stored.AuthorityHash = digest value.ExpectedAuthorityHash

    let targetSeal (value: TombstonePruneProposal) : WitnessPruneSeal =
        {
            CutoffSequence = value.CutoffSequence
            CutoffHash = digest value.CutoffHash
            TargetCount = value.TargetCount
            TargetDigest = digest value.TargetDigest
        }

    let matchesCopySeal (value: TombstonePruneProposal) (copy: ManagedCopyInventorySeal) =
        copy.CaseId = value.CaseId
        && copy.WitnessCutoffSequence = value.CutoffSequence
        && copy.WitnessCutoffHash = digest value.CutoffHash
        && copy.InventorySha256.Length = 32
        && copy.CopyCount >= 0L

    let matchesStoredFields (value: TombstonePruneProposal) (stored: StoredWitnessPruneReceipt) =
        stored.EventId = value.EventId
        && stored.CaseId = value.CaseId
        && stored.PurgeEventId = value.PurgeEventId
        && stored.PurgeWitnessSequence = value.PurgeWitnessSequence
        && stored.PurgeWitnessEpoch = value.PurgeWitnessEpoch
        && stored.PurgeWitnessHash = digest value.PurgeWitnessHash
        && stored.CutoffSequence = value.CutoffSequence
        && stored.CutoffHash = digest value.CutoffHash
        && stored.TargetCount = value.TargetCount
        && stored.TargetDigest = digest value.TargetDigest
        && stored.AuthorityRevision = value.ExpectedAuthorityRevision
        && stored.AuthorityHash = digest value.ExpectedAuthorityHash
        && stored.ValidUntil = value.ValidUntil
        && stored.IntentSequence > value.CutoffSequence

    let matchesStored value (canonical: byte array) stored =
        matchesStoredFields value stored
        && stored.Canonical = canonical
        && stored.CandidateHash = SHA256.HashData(canonical)

    let private targetMatches (witness: WitnessProtocol) (proposal: TombstonePruneProposal) ct =
        task {
            let seal = targetSeal proposal

            let! observed =
                CaseWitnessPayloadTargets.scan
                    witness
                    proposal.CaseId
                    seal.CutoffSequence
                    seal.CutoffHash
                    (fun _ -> Task.FromResult())
                    ct

            if
                observed.TargetCount = seal.TargetCount
                && observed.TargetDigest = seal.TargetDigest
            then
                return Some seal
            else
                return None
        }

    let preflight
        connection
        transaction
        (witness: WitnessProtocol)
        authorityRevision
        (stored: StoredCaseTombstone)
        (proposal: TombstonePruneProposal)
        observedAt
        ct
        =
        task {
            let! holds = CaseTombstoneRead.activeHolds connection transaction proposal.CaseId

            if not holds.IsEmpty then
                return Error(OwnerWitnessPruneOutcome.Refused LifecycleRefusal.HoldActive)
            else
                do!
                    witness.VerifyAuthorityEvidenceForCase(
                        stored.PurgeEventId,
                        stored.PurgeWitnessSequence,
                        stored.PurgeWitnessEpoch,
                        stored.PurgeWitnessHash,
                        stored.PurgeCandidateHash,
                        proposal.CaseId,
                        ct
                    )

                let! approvals =
                    CaseTombstonePruneOwnerApprovals.read
                        connection
                        transaction
                        witness
                        authorityRevision
                        proposal
                        true
                        observedAt
                        ct

                let! matches = targetMatches witness proposal ct

                match matches with
                | None ->
                    return
                        Error(
                            OwnerWitnessPruneOutcome.Refused
                                LifecycleRefusal.ErasureEvidenceIncomplete
                        )
                | Some seal -> return Ok(approvals, seal)
        }
