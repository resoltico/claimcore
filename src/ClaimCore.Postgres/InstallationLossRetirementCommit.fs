namespace ClaimCore.Postgres

open System.Data
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

/// One exact irreversible W0 → primary receipt → W1 sequence. Lost responses after
/// W0 remain uncertain and can only be reconciled with the same signed bytes.
module internal InstallationLossRetirementCommit =
    let private persistPrimary
        (primaryOwner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        alreadyRetired
        (input: LossRetirementCommitInput)
        intent
        commitments
        =
        if alreadyRetired then
            transaction.Rollback()
        else
            InstallationLossRetirementPrimary.insert
                primaryOwner
                transaction
                input.Decision
                input.DecisionBytes
                input.OwnerSignatureOne
                input.OwnerSignatureTwo
                intent
                commitments

            transaction.Commit()

    let private settledOutcome ownerWitnessConnection (witness: WitnessProtocol) input intent =
        let value = input.Decision

        let settlement =
            InstallationLossRetirementWitness.settle
                ownerWitnessConnection
                witness
                value
                input.DecisionBytes
                intent

        if InstallationLossRetirementState.committedWitness witness value settlement then
            InstallationLossRetirementOutcome.Retired(
                value.RetirementId,
                settlement.Sequence,
                settlement.EntryHash
            )
        else
            InstallationLossRetirementOutcome.Unconfirmed value.RetirementId

    let private settleUnderBarrier
        (primaryOwner: NpgsqlConnection)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (input: LossRetirementCommitInput)
        intent
        commitments
        =
        // W1 follows the primary COMMIT, so a new authority lock serializes it
        // against the owner full audit of a pending W0/primary pair.
        use barrier = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

        use authority =
            new NpgsqlCommand(
                "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR UPDATE",
                primaryOwner,
                barrier
            )

        if not (authority.ExecuteScalar() :? int64) then
            invalidOp "Loss retirement settlement authority is unavailable."

        let value = input.Decision

        let outcome =
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
                InstallationLossRetirementOutcome.AwaitingPrimary(
                    value.RetirementId,
                    intent.Sequence,
                    intent.EntryHash
                )
            else
                settledOutcome ownerWitnessConnection witness input intent

        barrier.Rollback()
        outcome

    let internal finish
        primaryOwner
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (input: LossRetirementCommitInput)
        intent
        commitments
        =
        settleUnderBarrier primaryOwner ownerWitnessConnection witness input intent commitments

    let private execute
        (primaryOwner: NpgsqlConnection)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        suppression
        (input: LossRetirementCommitInput)
        (witnessStarted: bool ref)
        =
        OwnerConnection.requireIdentity primaryOwner
        SchemaBaseline.requireCurrent primaryOwner
        witness.AdmitReadOnly()
        use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

        let commitments, alreadyRetired =
            InstallationLossRetirementPreflight.check
                primaryOwner
                transaction
                witness
                suppression
                input

        witnessStarted.Value <- true

        let intent =
            InstallationLossRetirementWitness.prepare
                ownerWitnessConnection
                witness
                input.Decision
                input.DecisionBytes
                input.OwnerSignatureOne
                input.OwnerSignatureTwo

        persistPrimary primaryOwner transaction alreadyRetired input intent commitments
        finish primaryOwner ownerWitnessConnection witness input intent commitments

    let record
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
        match InstallationLossRetirementCandidate.parse canonical with
        | None -> InstallationLossRetirementOutcome.Refused
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
                execute primaryOwner ownerWitnessConnection witness suppression input witnessStarted
            with _ ->
                if witnessStarted.Value then
                    InstallationLossRetirementOutcome.Unconfirmed value.RetirementId
                else
                    InstallationLossRetirementOutcome.Refused
