namespace ClaimCore.Postgres

open System.Data
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

/// One exact irreversible W0 → primary receipt → W1 sequence. Lost responses after
/// W0 remain uncertain and can only be reconciled with the same signed bytes.
module internal InstallationLossRetirementCommit =
    let private settledOutcome
        afterWitnessSettlement
        ownerWitnessConnection
        (witness: WitnessProtocol)
        input
        intent
        =
        task {
            let value = input.Decision

            let! settlement =
                InstallationLossRetirementWitness.settle
                    ownerWitnessConnection
                    witness
                    value
                    input.DecisionBytes
                    intent
                    CancellationToken.None

            afterWitnessSettlement ()

            let! committed =
                InstallationLossRetirementState.committedWitness witness value settlement

            if committed then
                return
                    InstallationLossRetirementOutcome.Retired(
                        value.RetirementId,
                        settlement.Sequence,
                        settlement.EntryHash
                    )
            else
                return InstallationLossRetirementOutcome.Unconfirmed value.RetirementId
        }

    let private settleExactPrimary
        afterWitnessSettlement
        primaryOwner
        barrier
        ownerWitnessConnection
        witness
        (input: LossRetirementCommitInput)
        intent
        commitments
        =
        task {
            let value = input.Decision

            if
                not (
                    InstallationLossRetirementPrimary.exactLocked
                        primaryOwner
                        barrier
                        value
                        input.DecisionBytes
                        input.OwnerSignatureOne
                        input.OwnerSignatureTwo
                        intent
                        commitments
                )
            then
                return
                    InstallationLossRetirementOutcome.AwaitingPrimary(
                        value.RetirementId,
                        intent.Sequence,
                        intent.EntryHash
                    )
            else
                return!
                    settledOutcome
                        afterWitnessSettlement
                        ownerWitnessConnection
                        witness
                        input
                        intent
        }

    let private settleUnderBarrier
        afterWitnessSettlement
        (primaryOwner: NpgsqlConnection)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (input: LossRetirementCommitInput)
        intent
        commitments
        =
        task {
            // W1 follows the primary COMMIT, so a new authority lock serializes it
            // against the owner full audit of a pending W0/primary pair.
            use barrier = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

            use authority =
                new NpgsqlCommand(
                    "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR UPDATE",
                    primaryOwner,
                    barrier
                )

            let! revision = authority.ExecuteScalarAsync(CancellationToken.None)

            if not (revision :? int64) then
                invalidOp "Loss retirement settlement authority is unavailable."

            let! outcome =
                settleExactPrimary
                    afterWitnessSettlement
                    primaryOwner
                    barrier
                    ownerWitnessConnection
                    witness
                    input
                    intent
                    commitments

            do! barrier.RollbackAsync(CancellationToken.None)
            return outcome
        }

    let internal finish primaryOwner ownerWitnessConnection witness input intent commitments =
        task {
            use! _authorityFence =
                AuthorityOperationFence.acquireShared None primaryOwner CancellationToken.None

            return!
                settleUnderBarrier
                    ignore
                    primaryOwner
                    ownerWitnessConnection
                    witness
                    input
                    intent
                    commitments
        }

    let private commitDecision
        primaryOwner
        transaction
        ownerWitnessConnection
        witness
        (input: LossRetirementCommitInput)
        commitments
        alreadyRetired
        (witnessStarted: bool ref)
        =
        task {
            witnessStarted.Value <- true

            let! intent =
                InstallationLossRetirementWitness.prepare
                    ownerWitnessConnection
                    witness
                    input.Decision
                    input.DecisionBytes
                    input.OwnerSignatureOne
                    input.OwnerSignatureTwo
                    CancellationToken.None

            do!
                InstallationLossRetirementPrimary.commitReceipt
                    primaryOwner
                    transaction
                    alreadyRetired
                    input.Decision
                    input.DecisionBytes
                    input.OwnerSignatureOne
                    input.OwnerSignatureTwo
                    intent
                    commitments

            return intent
        }

    let private execute
        afterWitnessSettlement
        (primaryOwner: NpgsqlConnection)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        suppression
        (input: LossRetirementCommitInput)
        (witnessStarted: bool ref)
        =
        task {
            OwnerConnection.requireIdentity primaryOwner
            SchemaBaseline.requireCurrent primaryOwner
            do! witness.AdmitReadOnly(CancellationToken.None)

            use! _authorityFence =
                AuthorityOperationFence.acquireExclusive None primaryOwner CancellationToken.None

            use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

            let! commitments, alreadyRetired =
                InstallationLossRetirementPreflight.check
                    primaryOwner
                    transaction
                    witness
                    suppression
                    input

            let! intent =
                commitDecision
                    primaryOwner
                    transaction
                    ownerWitnessConnection
                    witness
                    input
                    commitments
                    alreadyRetired
                    witnessStarted

            return!
                settleUnderBarrier
                    afterWitnessSettlement
                    primaryOwner
                    ownerWitnessConnection
                    witness
                    input
                    intent
                    commitments
        }

    let internal recordWithSettlementObservation
        afterWitnessSettlement
        (primaryOwner: NpgsqlConnection)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (suppression: ISuppressionCommitments)
        canonical
        signatureOne
        signatureTwo
        knownOperations
        (evidenceReport: byte array option)
        (checkpoint: byte array option)
        =
        task {
            match InstallationLossRetirementCandidate.parse canonical with
            | None -> return InstallationLossRetirementOutcome.Refused
            | Some value ->
                let input =
                    {
                        Decision = value
                        DecisionBytes = canonical
                        OwnerSignatureOne = signatureOne
                        OwnerSignatureTwo = signatureTwo
                        OperationIdentitySource = knownOperations
                        ReportEvidence = evidenceReport
                        CheckpointEvidence = checkpoint
                    }

                let witnessStarted = ref false

                try
                    return!
                        execute
                            afterWitnessSettlement
                            primaryOwner
                            ownerWitnessConnection
                            witness
                            suppression
                            input
                            witnessStarted
                with _ ->
                    if witnessStarted.Value then
                        return InstallationLossRetirementOutcome.Unconfirmed value.RetirementId
                    else
                        return InstallationLossRetirementOutcome.Refused
        }

    let record (primaryOwner: NpgsqlConnection) =
        recordWithSettlementObservation ignore primaryOwner
