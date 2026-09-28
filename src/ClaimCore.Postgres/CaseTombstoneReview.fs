namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

/// Steward review recomputes the target seal from the complete witness chain; SQL target rows
/// and a caller's claimed count/digest are never treated as classification authority.
module internal CaseTombstoneReview =
    let private verifyPurge
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (stored: StoredCaseTombstone)
        =
        task {
            if
                stored.PurgeCandidateHash <> SHA256.HashData(stored.PurgeCanonical)
                || stored.PurgeWitnessEpoch <> witness.Identity.Epoch
                || stored.AuthorityHash.Length <> 32
            then
                raise (InvalidDataException("Erasure purge identity is inconsistent."))

            do!
                CaseWitnessAuditEvidence.verify
                    connection
                    transaction
                    witness
                    cutoff
                    stored.CaseId
                    stored.PurgeEventId
                    stored.PurgeWitnessSequence
                    stored.PurgeWitnessEpoch
                    stored.PurgeWitnessHash
                    stored.PurgeCandidateHash
                    ClaimCore.Witness.SettledAuthority
        }

    let private targetSeal
        connection
        transaction
        (witness: WitnessProtocol)
        (caseId: Guid)
        (stored: StoredCaseTombstone)
        (snapshot: ClaimCore.Witness.Snapshot)
        =
        task {
            match stored.PruneEventId with
            | None ->
                let seal =
                    CaseWitnessPayloadTargets.scan
                        witness
                        caseId
                        snapshot.TipSequence
                        snapshot.TipHash
                        ignore

                return seal, false
            | Some _ ->
                let! proof =
                    CaseTombstonePruneReceiptAudit.verify
                        connection
                        transaction
                        witness
                        snapshot.TipSequence
                        caseId

                if proof.IsNone then
                    raise (InvalidDataException("Prune receipt was not independently verified."))

                let! found = CaseTombstonePrunePrimaryRead.find connection transaction caseId

                let receipt =
                    found
                    |> Option.defaultWith (fun () ->
                        raise (InvalidDataException("Prune receipt is absent.")))

                return
                    {
                        CutoffSequence = receipt.CutoffSequence
                        CutoffHash = receipt.CutoffHash
                        TargetCount = receipt.TargetCount
                        TargetDigest = receipt.TargetDigest
                    },
                    true
        }

    let private summary
        (stored: StoredCaseTombstone)
        (seal: WitnessPruneSeal)
        pruned
        (holds: (Guid * DateOnly) list)
        =
        let privacy =
            CaseLifecycleCandidate.parsePrivacy stored.Phase
            |> Option.defaultWith (fun () ->
                raise (InvalidDataException("Tombstone privacy phase is invalid.")))

        if privacy <> PrivacyPhase.ErasurePending && not pruned then
            raise (InvalidDataException("Terminal tombstone lacks witness prune proof."))

        TombstoneReviewOutcome.Available
            {
                CaseId = stored.CaseId
                PrivacyPhase = privacy
                PurgeEventId = stored.PurgeEventId
                PurgeWitnessSequence = stored.PurgeWitnessSequence
                PurgeWitnessEpoch = stored.PurgeWitnessEpoch
                PurgeWitnessHash = Convert.ToHexStringLower stored.PurgeWitnessHash
                CutoffSequence = seal.CutoffSequence
                CutoffHash = Convert.ToHexStringLower seal.CutoffHash
                TargetCount = seal.TargetCount
                TargetDigest = Convert.ToHexStringLower seal.TargetDigest
                AuthorityRevision = stored.AuthorityRevision
                AuthorityHash = Convert.ToHexStringLower stored.AuthorityHash
                ActiveHolds =
                    holds
                    |> List.map (fun (holdId, reviewOn) -> { HoldId = holdId; ReviewOn = reviewOn })
                RequiredDistinctStewardApprovals =
                    if privacy = PrivacyPhase.ErasureFinal then 0 else 2
                WitnessPayloadPruned = pruned
                ManagedCopyCertificationPending = privacy = PrivacyPhase.ErasurePending
            }

    let private read dataSource (witness: WitnessProtocol) (context: ActorCallContext) caseId =
        task {
            use! connection = RuntimeDatabase.openConnectionAsync dataSource
            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

            let! revision =
                ActorGrantRead.lockRevision
                    connection
                    transaction
                    false
                    System.Threading.CancellationToken.None

            let! found = CaseTombstoneRead.lock connection transaction caseId

            match found with
            | None -> return TombstoneReviewOutcome.ResourceUnavailable
            | Some stored ->
                let! allowed =
                    ActorMutationGuard.authorizeScope
                        connection
                        transaction
                        context
                        (ResourceScope.Case caseId)
                        revision

                if not allowed then
                    return TombstoneReviewOutcome.ResourceUnavailable
                else
                    let snapshot = witness.Snapshot()
                    do! verifyPurge connection transaction witness snapshot.TipSequence stored
                    do! CaseErasurePurgeDelete.verifyAbsent connection transaction caseId

                    let! seal, pruned =
                        targetSeal connection transaction witness caseId stored snapshot

                    let! holds = CaseTombstoneRead.activeHolds connection transaction caseId

                    return summary stored seal pruned holds
        }

    let review dataSource (witness: WitnessProtocol) (context: ActorCallContext) caseId =
        task {
            if
                context.Action <> EndpointAction.ReviewTombstone
                || context.CaseId <> Some caseId
            then
                return TombstoneReviewOutcome.ResourceUnavailable
            else
                try
                    witness.Admit()
                    return! read dataSource witness context caseId
                with
                | :? InvalidDataException ->
                    return TombstoneReviewOutcome.Failed CoreFault.StoreIntegrityError
                | _ -> return TombstoneReviewOutcome.Failed CoreFault.StoreUnavailable
        }
