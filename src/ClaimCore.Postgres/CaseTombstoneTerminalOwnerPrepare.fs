namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open OwnerTerminalDecisionData
open WitnessProtocolReconciliation

module internal CaseTombstoneTerminalOwnerPrepare =
    let private digest proposal =
        let bytes = CaseTombstoneTerminalCandidate.proposal proposal

        try
            SHA256.HashData(bytes) |> Convert.ToHexStringLower
        finally
            CryptographicOperations.ZeroMemory(bytes)

    let private projection (stored: StoredTerminalTombstone) =
        {
            SourceRevision = stored.SourceRevision
            Disposition = stored.Disposition
            Privacy =
                CaseLifecycleCandidate.parsePrivacy stored.Phase
                |> Option.defaultWith (fun () -> invalidOp "Terminal privacy phase is invalid.")
            ReferenceCommitment = stored.ReferenceCommitment
        }

    let private copy
        (provider: ICopyErasureCertification)
        connection
        transaction
        witness
        (value: TerminalCopyProposal)
        observedAt
        (ct: CancellationToken)
        =
        provider.RequireAllAbsent(
            connection,
            transaction,
            witness,
            value.CaseId,
            value.PruneEventId,
            value.WitnessCutoffSequence,
            Convert.FromHexString value.WitnessCutoffHash,
            value.PolicyId,
            value.SuppressionUntil,
            value.ExpectedWriterGeneration,
            observedAt,
            ct
        )

    let private fence
        (provider: IRecoveryFenceCertification)
        connection
        transaction
        witness
        (copy: OwnerCopyAbsenceCertificate)
        proposal
        observedAt
        (ct: CancellationToken)
        =
        match proposal with
        | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ -> task { return Some None }
        | TombstoneTerminalProposal.CompleteSuppressionHorizon final ->
            task {
                let! evidence =
                    provider.RequireVerifiedFence(
                        connection,
                        transaction,
                        witness,
                        final.Copy.CaseId,
                        copy,
                        final,
                        observedAt,
                        ct
                    )

                return evidence |> Option.map Some
            }

    let private decide
        (stored: StoredTerminalTombstone)
        proposal
        observedAt
        approvals
        (certificate: OwnerCopyAbsenceCertificate)
        (evidence: OwnerRecoveryFenceCertificate option)
        =
        let eventDigest = digest proposal

        match
            OwnerTerminalDecisionKernel.evaluate
                proposal
                (projection stored)
                eventDigest
                observedAt
                approvals
                certificate.Facts
                (evidence |> Option.map _.Facts)
        with
        | Error refusal -> Error(OwnerTerminalOutcome.Refused refusal)
        | Ok phase ->
            Ok
                {
                    Copy = certificate
                    Fence = evidence
                    Approvals = approvals
                    Stored = stored
                    ObservedAt = observedAt
                    EventDigest = eventDigest
                    NextPhase = phase
                }

    let private checkedEvidence
        connection
        transaction
        (witness: WitnessProtocol)
        copyProvider
        fenceProvider
        (stored: StoredTerminalTombstone)
        proposal
        observedAt
        approvals
        ct
        =
        task {
            let value = TombstoneTerminalProposal.copy proposal

            let! pendingPublication =
                DataAuditExternalPublications.hasUnadoptedForCase
                    connection
                    transaction
                    value.CaseId

            let! verifiedCopy =
                if pendingPublication then
                    task { return None }
                else
                    copy copyProvider connection transaction witness value observedAt ct

            match verifiedCopy with
            | None -> return Error OwnerTerminalOutcome.InventoryUnknown
            | Some certificate ->
                witness.VerifyHistoricalTip(
                    certificate.WitnessTipSequence,
                    certificate.WitnessTipHash
                )

                let! verifiedFence =
                    fence
                        fenceProvider
                        connection
                        transaction
                        witness
                        certificate
                        proposal
                        observedAt
                        ct

                match verifiedFence with
                | None -> return Error OwnerTerminalOutcome.RecoveryFenceUnknown
                | Some evidence ->
                    return decide stored proposal observedAt approvals certificate evidence
        }

    let prepare
        connection
        transaction
        (witness: WitnessProtocol)
        copyProvider
        fenceProvider
        actorAuthorityRevision
        (stored: StoredTerminalTombstone)
        proposal
        (observedAt: DateTimeOffset)
        (ct: CancellationToken)
        =
        task {
            let value = TombstoneTerminalProposal.copy proposal

            if
                not (CaseTombstoneTerminalPolicy.validProposal proposal observedAt)
                || not (CaseTombstoneTerminalPolicy.matches stored proposal)
            then
                return Error(OwnerTerminalOutcome.Refused LifecycleRefusal.VersionConflict)
            else
                let! holds = CaseTombstoneRead.activeHolds connection transaction value.CaseId

                if not holds.IsEmpty then
                    return Error(OwnerTerminalOutcome.Refused LifecycleRefusal.HoldActive)
                else
                    do! CaseErasurePurgeDelete.verifyAbsent connection transaction value.CaseId

                    let! approvals =
                        CaseTombstoneTerminalOwnerApprovals.read
                            connection
                            transaction
                            witness
                            actorAuthorityRevision
                            proposal
                            observedAt

                    return!
                        checkedEvidence
                            connection
                            transaction
                            witness
                            copyProvider
                            fenceProvider
                            stored
                            proposal
                            observedAt
                            approvals
                            ct
        }
