namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal WriterHandoffOwnerOutcome =
    | Prepared of handoffId: Guid * witnessSequence: int64 * witnessHash: byte array
    | Settled of handoffId: Guid * witnessSequence: int64 * witnessHash: byte array
    | Refused
    | Unconfirmed of handoffId: Guid

/// Owner-only PREPARE. The independent evidence verifier is a trusted composition dependency,
/// never a CLI Boolean or an arbitrary callback selected by a caller.
module internal WriterHandoffOwnerPreparation =
    let private primaryGeneration
        (connection: NpgsqlConnection)
        transaction
        (witness: WitnessProtocol)
        =
        use command =
            new NpgsqlCommand(
                "SELECT installation_id,lineage_id,witness_epoch,writer_generation "
                + "FROM claimcore.installation_lineage WHERE singleton",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Primary writer authority is absent."

        let generation = reader.GetInt64(3)

        if
            reader.GetGuid(0) <> witness.Identity.InstallationId
            || reader.GetGuid(1) <> witness.Identity.LineageId
            || reader.GetInt64(2) <> witness.Identity.Epoch
            || reader.Read()
        then
            invalidOp "Primary writer authority diverged."

        generation

    let private evidenceMatches
        (value: WriterHandoffPreparation)
        (evidence: WriterHandoffQualifiedEvidence)
        now
        =
        evidence.PublicationManifestSha256.Length = 32
        && evidence.IndependentProbeNonceSha256.Length = 32
        && evidence.InstallationId = value.InstallationId
        && evidence.LineageId = value.LineageId
        && evidence.Epoch = value.Epoch
        && evidence.OldGeneration = value.OldGeneration
        && evidence.ReviewedCutoffSequence = value.ReviewedCutoffSequence
        && evidence.ReviewedCutoffHash = value.ReviewedCutoffHash
        && evidence.NewCapabilitySha256 = value.NewCapabilitySha256
        && evidence.RestoreReportSha256 = value.RestoreReportSha256
        && evidence.FenceReportSha256 = value.FenceReportSha256
        && evidence.InventorySha256 = value.InventorySha256
        && evidence.CheckpointSigningKeyId = value.CheckpointSigningKeyId
        && evidence.ValidUntil > now
        && value.ValidUntil > now
        && value.ValidUntil <= evidence.ValidUntil

    let private fullAudit dataSource (witness: WitnessProtocol) commitments expected =
        task {
            use! audit = RuntimeDatabase.openConnectionAsync dataSource

            let! summary =
                DataAudit.runWithSuppression audit witness commitments CancellationToken.None

            return summary.PendingIntents = 0L && summary.WitnessCutoff = expected
        }

    let private preflight
        connection
        transaction
        dataSource
        (witness: WitnessProtocol)
        (verifier: IWriterHandoffEvidenceVerifier)
        commitments
        (value: WriterHandoffPreparation)
        canonical
        signature
        (newCapability: byte array)
        =
        task {
            let! _ =
                ActorGrantRead.lockRevision connection transaction true CancellationToken.None

            let! now = ManagedCopySignerPolicy.databaseNow connection transaction
            let snapshot = witness.Snapshot()
            let! verified = verifier.VerifyPreparation(value, canonical, CancellationToken.None)

            if
                snapshot.HandoffPending
                || snapshot.WriterGeneration <> value.OldGeneration
                || snapshot.TipSequence <> value.ExpectedTipSequence
                || snapshot.TipHash <> value.ExpectedTipHash
                || primaryGeneration connection transaction witness <> value.OldGeneration
                || SHA256.HashData(newCapability) <> value.NewCapabilitySha256
                || verified.IsNone
                || not (evidenceMatches value verified.Value now)
            then
                return false
            else
                WriterHandoffCutoff.verify witness value
                let! audit = fullAudit dataSource witness commitments value.ExpectedTipSequence

                if not audit then
                    return false
                else
                    do!
                        WriterHandoffOwnerChecks.verify
                            connection
                            transaction
                            witness
                            value
                            canonical
                            signature
                            now
                            false
                            CancellationToken.None

                    return true
        }

    let private commitPrepared
        (primaryOwner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (value: WriterHandoffPreparation)
        canonical
        signature
        oldCapability
        (started: bool ref)
        =
        task {
            started.Value <- true

            let ticket =
                WriterHandoffWitnessCommands.prepare
                    ownerWitnessConnection
                    witness
                    value
                    canonical
                    signature
                    oldCapability

            WriterHandoffOwnerWrite.preparation
                primaryOwner
                transaction
                value
                canonical
                signature
                ticket

            WriterHandoffOwnerWrite.consume primaryOwner transaction value
            do! transaction.CommitAsync()

            return
                WriterHandoffOwnerOutcome.Prepared(
                    value.HandoffId,
                    ticket.Sequence,
                    ticket.EntryHash
                )
        }

    let private prepareFresh
        (primaryOwner: NpgsqlConnection)
        dataSource
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (verifier: IWriterHandoffEvidenceVerifier)
        (commitments: ISuppressionCommitments option)
        (value: WriterHandoffPreparation)
        canonical
        signature
        oldCapability
        newCapability
        (started: bool ref)
        =
        task {
            witness.Admit()
            use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

            let! allowed =
                preflight
                    primaryOwner
                    transaction
                    dataSource
                    witness
                    verifier
                    commitments
                    value
                    canonical
                    signature
                    newCapability

            if not allowed then
                return WriterHandoffOwnerOutcome.Refused
            else
                return!
                    commitPrepared
                        primaryOwner
                        transaction
                        ownerWitnessConnection
                        witness
                        value
                        canonical
                        signature
                        oldCapability
                        started
        }

    let prepare
        (primaryOwner: NpgsqlConnection)
        dataSource
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (verifier: IWriterHandoffEvidenceVerifier)
        (commitments: ISuppressionCommitments option)
        (canonical: byte array)
        (signature: byte array)
        (oldCapability: byte array)
        (newCapability: byte array)
        =
        task {
            match WriterHandoffPreparation.parse canonical with
            | None -> return WriterHandoffOwnerOutcome.Refused
            | Some value ->
                let started = ref false

                try
                    OwnerConnection.requireIdentity primaryOwner
                    SchemaBaseline.requireCurrent primaryOwner
                    let existing = witness.EvidenceStore.TryReadHandoff(value.HandoffId)

                    if existing.IsSome then
                        return WriterHandoffOwnerOutcome.Unconfirmed value.HandoffId
                    else
                        return!
                            prepareFresh
                                primaryOwner
                                dataSource
                                ownerWitnessConnection
                                witness
                                verifier
                                commitments
                                value
                                canonical
                                signature
                                oldCapability
                                newCapability
                                started
                with _ ->
                    return
                        if started.Value then
                            WriterHandoffOwnerOutcome.Unconfirmed value.HandoffId
                        else
                            WriterHandoffOwnerOutcome.Refused
        }
