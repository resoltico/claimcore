namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open WitnessProtocolReconciliation

/// The witness intent precedes the primary commit; settlement follows it. Every ambiguous
/// boundary is reported Unconfirmed with the same caller-stable event ID.
module internal CaseErasurePurgeCommit =
    let private invalid () =
        raise (InvalidDataException("Owner purge witness cutoff moved."))

    let private settle
        (witness: WitnessProtocol)
        (change: LifecycleChange)
        (intent: WitnessIntent)
        canonical
        =
        witness.ReconcileAuthority(
            change.EventId,
            intent.Ticket.Sequence,
            intent.Ticket.Epoch,
            intent.Ticket.EntryHash,
            canonical
        )

    let private candidate
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (requestTombstone: StoredErasurePurge)
        (commitments: ISuppressionCommitments)
        (prepared: PreparedErasurePurge)
        (seal: WitnessDenialSeal)
        instant
        =
        let referenceKeyed = commitments.Reference change.CaseReference

        let canonical =
            CaseErasurePurgeCandidate.encode
                change
                projection.CaseId
                commitments.KeyId
                referenceKeyed
                (Claim.view projection.Claim).Version
                projection.Sequence
                projection.EventHash
                prepared.RequestEventId
                requestTombstone.RequestDenialCount
                requestTombstone.RequestDenialDigest
                prepared.RequestCommitment
                prepared.ProposalCommitment
                seal
                prepared.CopySeal
                prepared.Approvals
                instant

        referenceKeyed, canonical

    let private persist
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (prepared: PreparedErasurePurge)
        (seal: WitnessDenialSeal)
        referenceKeyed
        (canonical: byte array)
        instant
        (intentAttempted: bool ref)
        (ct: CancellationToken)
        =
        task {
            try
                intentAttempted.Value <- true

                let intent =
                    witness.BeginAuthority(change.EventId, canonical, Some projection.CaseId)

                let proof =
                    {
                        Projection = projection
                        Change = change
                        RequestEventId = prepared.RequestEventId
                        ReferenceCommitment = referenceKeyed
                        SuppressionKeyId = commitments.KeyId
                        RequestCandidateCommitment = prepared.RequestCommitment
                        ProposalCommitment = prepared.ProposalCommitment
                        WitnessSeal = seal
                        CopySeal = prepared.CopySeal
                        Approvals = prepared.Approvals
                        CanonicalAction = canonical
                        Intent = intent
                        ObservedAt = instant
                    }

                do! CaseErasurePurgeWrite.persist connection transaction proof
                let! deleted = CaseErasurePurgeDelete.purge connection transaction projection.CaseId
                do! transaction.CommitAsync(ct)

                settle witness change intent canonical

                return OwnerPurgeOutcome.Purged(change.EventId, deleted)
            finally
                CryptographicOperations.ZeroMemory(canonical)

                prepared.Approvals
                |> List.iter (fun value -> CryptographicOperations.ZeroMemory(value.Canonical))
        }

    let private completePrepared
        connection
        transaction
        witness
        commitments
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (requestTombstone: StoredErasurePurge)
        (tip: ClaimCore.Witness.Snapshot)
        instant
        intentAttempted
        (stage: string ref)
        ct
        (prepared: PreparedErasurePurge)
        =
        task {
            stage.Value <- "WITNESS_COVERAGE"

            let seal =
                CaseErasureWitnessDenials.scan
                    connection
                    transaction
                    witness
                    commitments
                    projection.CaseId
                    tip.TipSequence

            let currentTip = witness.Snapshot()

            if currentTip.TipSequence <> tip.TipSequence || currentTip.TipHash <> tip.TipHash then
                invalid ()

            let referenceKeyed, canonical =
                candidate projection change requestTombstone commitments prepared seal instant

            return!
                persist
                    connection
                    transaction
                    witness
                    commitments
                    projection
                    change
                    prepared
                    seal
                    referenceKeyed
                    canonical
                    instant
                    intentAttempted
                    ct
        }

    let fresh
        connection
        transaction
        witness
        commitments
        inventory
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (requestTombstone: StoredErasurePurge)
        draft
        (tip: ClaimCore.Witness.Snapshot)
        instant
        intentAttempted
        (stage: string ref)
        ct
        =
        task {
            let! prepared =
                CaseErasurePurgePreparation.prepare
                    connection
                    transaction
                    witness
                    commitments
                    inventory
                    projection
                    change
                    draft
                    tip
                    instant
                    ct

            match prepared with
            | Error outcome -> return outcome
            | Ok prepared ->
                return!
                    completePrepared
                        connection
                        transaction
                        witness
                        commitments
                        projection
                        change
                        requestTombstone
                        tip
                        instant
                        intentAttempted
                        stage
                        ct
                        prepared
        }
