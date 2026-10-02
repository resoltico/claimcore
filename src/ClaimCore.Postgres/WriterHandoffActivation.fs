namespace ClaimCore.Postgres

open System
open System.Data
open System.Threading
open Npgsql
open ClaimCore.Application

/// Owner-only restored-writer release after exact W1 and externally qualified final tail.
module internal WriterHandoffActivation =
    [<NoEquality; NoComparison>]
    type private ActivationReadiness =
        | Historical
        | Fresh of WriterActivationQualification * bool

    let private auditCutoff
        (dataSource: NpgsqlDataSource)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments option)
        (expected: int64)
        (ct: CancellationToken)
        =
        task {
            use! audit = RuntimeDatabase.openConnectionAsync dataSource
            let! result = DataAudit.runWithSuppression audit witness commitments ct
            return result.PendingIntents = 0L && result.WitnessCutoff = expected
        }

    let private preflight
        (dataSource: NpgsqlDataSource)
        (witness: WitnessProtocol)
        (verifier: IWriterActivationEvidenceVerifier)
        (commitments: ISuppressionCommitments option)
        (value: WriterActivationEvidence)
        ct
        =
        task {
            let activationId = WriterActivationCandidate.activationId value.HandoffId
            let snapshot = witness.Snapshot()

            if not snapshot.ActivationPending then
                return
                    if snapshot.ActivationEventId = Some activationId then
                        Some Historical
                    else
                        None
            else
                let! proof = verifier.Verify(value, ct)

                match proof with
                | None -> return None
                | Some verified ->
                    if
                        snapshot.TipSequence <> value.W1Sequence || snapshot.TipHash <> value.W1Hash
                    then
                        return None
                    else
                        let! audited =
                            auditCutoff dataSource witness commitments value.W1Sequence ct

                        return if audited then Some(Fresh(verified, true)) else None
        }

    let private activated
        (state: PrimaryActivationState)
        (snapshot: ClaimCore.Witness.Snapshot)
        activationId
        (tickets: WriterActivationTickets)
        =
        not state.Pending
        && not snapshot.ActivationPending
        && state.ActivationId = Some activationId
        && state.ActivationSequence = Some tickets.Settlement.Sequence
        && state.ActivationHash = Some tickets.Settlement.EntryHash
        && snapshot.ActivationEventId = Some activationId
        && snapshot.ActivationSequence = Some tickets.Settlement.Sequence
        && snapshot.ActivationHash = Some tickets.Settlement.EntryHash

    let private readyForWitness
        (state: PrimaryActivationState)
        (snapshot: ClaimCore.Witness.Snapshot)
        activationId
        auditedW1
        (value: WriterActivationEvidence)
        =
        if state.Pending && snapshot.ActivationPending then
            auditedW1
            && snapshot.TipSequence = value.W1Sequence
            && snapshot.TipHash = value.W1Hash
        elif state.Pending && not snapshot.ActivationPending then
            snapshot.ActivationEventId = Some activationId
        else
            not state.Pending
            && not snapshot.ActivationPending
            && state.ActivationId = Some activationId

    let private persistPrimary
        (primaryOwner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterActivationEvidence)
        activationId
        canonical
        (tickets: WriterActivationTickets)
        pending
        =
        task {
            if pending then
                WriterActivationPrimary.insert
                    primaryOwner
                    transaction
                    value
                    activationId
                    canonical
                    tickets

                WriterActivationPrimary.release primaryOwner transaction value activationId tickets
                do! transaction.CommitAsync(CancellationToken.None)
            else
                WriterActivationPrimary.requireExisting
                    primaryOwner
                    transaction
                    activationId
                    canonical
                    tickets

                transaction.Rollback()
        }

    let private commitOrReconcile
        (primaryOwner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (value: WriterActivationEvidence)
        activationId
        canonical
        (state: PrimaryActivationState)
        (started: bool ref)
        =
        task {
            started.Value <- true

            let tickets =
                WriterActivationWitness.activate
                    ownerWitnessConnection
                    witness
                    value.HandoffId
                    activationId
                    value.W1Sequence
                    value.W1Hash
                    canonical

            do!
                persistPrimary
                    primaryOwner
                    transaction
                    value
                    activationId
                    canonical
                    tickets
                    state.Pending

            let updated = WriterActivationPrimary.current primaryOwner

            if activated updated (witness.Snapshot()) activationId tickets then
                return
                    WriterActivationOutcome.Activated(
                        activationId,
                        tickets.Settlement.Sequence,
                        tickets.Settlement.EntryHash
                    )
            else
                return WriterActivationOutcome.Unconfirmed activationId
        }

    let private checkedState primaryOwner transaction witness value readiness now =
        let state, snapshot =
            match readiness with
            | Historical ->
                WriterActivationEvidenceChecks.verifyHistorical
                    primaryOwner
                    transaction
                    witness
                    value
            | Fresh _ ->
                WriterActivationEvidenceChecks.verify primaryOwner transaction witness value now

        let qualifiedNow =
            match readiness with
            | Historical ->
                not snapshot.ActivationPending
                && snapshot.ActivationEventId =
                    Some(WriterActivationCandidate.activationId value.HandoffId)
            | Fresh(proof, _) -> WriterActivationEvidenceChecks.qualified value proof now

        state, snapshot, qualifiedNow

    let private underLock
        (primaryOwner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (value: WriterActivationEvidence)
        readiness
        (ct: CancellationToken)
        (started: bool ref)
        =
        task {
            let! _ = ActorGrantRead.lockRevision primaryOwner transaction true ct
            let! now = Sql.databaseNow primaryOwner transaction

            let state, snapshot, qualifiedNow =
                checkedState primaryOwner transaction witness value readiness now

            if not qualifiedNow then
                return WriterActivationOutcome.Refused
            else
                let activationId = WriterActivationCandidate.activationId value.HandoffId
                let canonical = WriterActivationCandidate.encode value

                let auditedW1 =
                    match readiness with
                    | Historical -> false
                    | Fresh(_, audited) -> audited

                try
                    if not (readyForWitness state snapshot activationId auditedW1 value) then
                        return WriterActivationOutcome.Refused
                    else
                        return!
                            commitOrReconcile
                                primaryOwner
                                transaction
                                ownerWitnessConnection
                                witness
                                value
                                activationId
                                canonical
                                state
                                started
                finally
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(canonical)
        }

    let activate
        (primaryOwner: NpgsqlConnection)
        (dataSource: NpgsqlDataSource)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (verifier: IWriterActivationEvidenceVerifier)
        (commitments: ISuppressionCommitments option)
        (value: WriterActivationEvidence)
        (ct: CancellationToken)
        =
        task {
            let activationId = WriterActivationCandidate.activationId value.HandoffId
            let started = ref false

            try
                OwnerConnection.requireIdentity primaryOwner
                SchemaBaseline.requireCurrent primaryOwner
                witness.AdmitReadOnly()
                use! _authorityFence = AuthorityOperationFence.acquireExclusive None primaryOwner ct
                let! readiness = preflight dataSource witness verifier commitments value ct

                match readiness with
                | None -> return WriterActivationOutcome.Refused
                | Some readiness ->
                    use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

                    return!
                        underLock
                            primaryOwner
                            transaction
                            ownerWitnessConnection
                            witness
                            value
                            readiness
                            ct
                            started
            with _ ->
                return
                    if started.Value then
                        WriterActivationOutcome.Unconfirmed activationId
                    else
                        WriterActivationOutcome.Refused
        }
