namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open DataAuditCommon

/// Exact nonpayload owner event replay. It verifies the witnessed historical decision and
/// retained signed-proof digest, not an impossible re-read of physically deleted copy bytes.
module internal CaseTombstoneTerminalEventAudit =
    let private transition previous =
        function
        | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ when previous = "ERASURE_PENDING" ->
            "CONFIRM_MANAGED_PAYLOAD_ABSENCE", "PAYLOAD_ERASED_SUPPRESSION_RETAINED"
        | TombstoneTerminalProposal.CompleteSuppressionHorizon _ when
            previous = "PAYLOAD_ERASED_SUPPRESSION_RETAINED"
            ->
            "COMPLETE_SUPPRESSION_HORIZON", "ERASURE_FINAL"
        | _ -> corrupt ()

    let private fields
        (event: StoredTerminalEvent)
        (decoded: TerminalEventDecoded)
        previousRevision
        (previousHash: byte array)
        expectedPreviousPhase
        =
        let copy = TombstoneTerminalProposal.copy decoded.Proposal

        let expectedAction, expectedPhase =
            transition expectedPreviousPhase decoded.Proposal

        event.EventId = copy.EventId
        && event.CaseId = copy.CaseId
        && event.ActionName = expectedAction
        && event.ResultingPhase = expectedPhase
        && event.PolicyId = copy.PolicyId
        && event.SuppressionUntil = copy.SuppressionUntil
        && event.PruneEventId = copy.PruneEventId
        && event.CopyInventoryDigest = Convert.FromHexString copy.CopyInventoryDigest
        && event.RelevantCopyCount = copy.RelevantCopyCount
        && event.WriterGeneration = copy.ExpectedWriterGeneration
        && event.CopyProofSha256 = decoded.CopyProofSha256
        && event.RecoveryFenceDigest =
            (match decoded.Proposal with
             | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ -> None
             | TombstoneTerminalProposal.CompleteSuppressionHorizon final ->
                 Some(Convert.FromHexString final.RecoveryFenceDigest))
        && event.ApprovalOneId = decoded.ApprovalOneId
        && event.ApprovalTwoId = decoded.ApprovalTwoId
        && event.ActorAuthorityRevision = decoded.ActorAuthorityRevision
        && event.PreviousAuthorityRevision = previousRevision
        && event.PreviousAuthorityHash = previousHash
        && event.AuthorityRevision = decoded.AuthorityRevision
        && event.AuthorityRevision = previousRevision + 1L
        && event.AuthorityHash =
            CaseTombstoneTerminalEventCandidate.eventHash previousHash event.Canonical
        && event.RecordedAt = decoded.ObservedAt

    let private witnessEvent (witness: WitnessProtocol) cutoff (event: StoredTerminalEvent) =
        if event.WitnessSequence > cutoff || event.WitnessEpoch <> witness.Identity.Epoch then
            corrupt ()

        witnessProof (fun () ->
            witness.VerifyAuthorityEvidenceForCase(
                event.EventId,
                event.WitnessSequence,
                event.WitnessEpoch,
                event.WitnessHash,
                event.CandidateHash,
                event.CaseId
            ))

    let verify
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        eventId
        previousRevision
        previousHash
        previousPhase
        hasActiveHold
        =
        task {
            let! found = CaseTombstoneTerminalEventRead.find connection transaction eventId
            let event = found |> Option.defaultWith corrupt

            let decoded =
                CaseTombstoneTerminalEventCodec.decode event.Canonical
                |> Option.defaultWith corrupt

            if
                hasActiveHold
                || event.CandidateHash <> SHA256.HashData(event.Canonical)
                || not (fields event decoded previousRevision previousHash previousPhase)
            then
                corrupt ()

            do!
                CaseTombstoneTerminalEventProof.verifyApprovals
                    connection
                    transaction
                    witness
                    event
                    decoded

            witnessEvent witness cutoff event
            return event.ResultingPhase, event.EventId, event.PolicyId, event.SuppressionUntil
        }
