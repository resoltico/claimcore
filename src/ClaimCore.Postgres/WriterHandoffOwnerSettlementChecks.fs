namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application

module internal WriterHandoffOwnerSettlementChecks =
    let private matchesPrepared
        (prepared: PrimaryWriterPreparation)
        (settlement: WriterHandoffSettlement)
        (newCapability: byte array)
        =
        let original = prepared.Value

        settlement.HandoffId = original.HandoffId
        && settlement.InstallationId = original.InstallationId
        && settlement.LineageId = original.LineageId
        && settlement.Epoch = original.Epoch
        && settlement.OldGeneration = original.OldGeneration
        && settlement.NewGeneration = original.NewGeneration
        && settlement.PrepareSequence = prepared.Intent.Sequence
        && settlement.PrepareHash = prepared.Intent.EntryHash
        && settlement.PrepareCanonicalSha256 = SHA256.HashData(prepared.Canonical)
        && settlement.NewCapabilitySha256 = original.NewCapabilitySha256
        && SHA256.HashData(newCapability) = original.NewCapabilitySha256
        && settlement.CheckpointSigningKeyId = original.CheckpointSigningKeyId
        && settlement.FenceReportSha256 = original.FenceReportSha256
        && settlement.InventorySha256 = original.InventorySha256
        && settlement.RestoreReportSha256 = original.RestoreReportSha256
        && settlement.ApprovalOneId = original.ApprovalOneId
        && settlement.ApprovalTwoId = original.ApprovalTwoId

    let private qualified
        (value: WriterHandoffSettlement)
        (prepared: WriterHandoffPreparation)
        (evidence: WriterHandoffQualifiedEvidence)
        now
        =
        evidence.PublicationManifestSha256.Length = 32
        && evidence.IndependentProbeNonceSha256.Length = 32
        && evidence.InstallationId = value.InstallationId
        && evidence.LineageId = value.LineageId
        && evidence.Epoch = value.Epoch
        && evidence.OldGeneration = value.OldGeneration
        && evidence.ReviewedCutoffSequence = prepared.ReviewedCutoffSequence
        && evidence.ReviewedCutoffHash = prepared.ReviewedCutoffHash
        && evidence.NewCapabilitySha256 = value.NewCapabilitySha256
        && evidence.RestoreReportSha256 = value.RestoreReportSha256
        && evidence.FenceReportSha256 = value.FenceReportSha256
        && evidence.InventorySha256 = value.InventorySha256
        && evidence.CheckpointSigningKeyId = value.CheckpointSigningKeyId
        && evidence.ValidUntil > now
        && value.ValidUntil > now

    let private currentPrimaryGeneration connection transaction =
        use command =
            new NpgsqlCommand(
                "SELECT writer_generation FROM claimcore.installation_lineage WHERE singleton",
                connection,
                transaction
            )

        match command.ExecuteScalar() with
        | :? int64 as value -> value
        | _ -> invalidOp "Primary writer generation is unavailable."

    let private pairMatches
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (snapshot: ClaimCore.Witness.Snapshot)
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffSettlement)
        (newCapability: byte array)
        =
        matchesPrepared prepared value newCapability
        && snapshot.HandoffPending
        && snapshot.WriterGeneration = value.OldGeneration
        && snapshot.TipSequence = prepared.Intent.Sequence
        && snapshot.TipHash = prepared.Intent.EntryHash
        && currentPrimaryGeneration connection transaction = value.OldGeneration

    let private verifySignatures
        connection
        transaction
        witness
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffSettlement)
        canonical
        signature
        now
        =
        task {
            do!
                WriterHandoffOwnerChecks.verify
                    connection
                    transaction
                    witness
                    prepared.Value
                    prepared.Canonical
                    prepared.Signature
                    now
                    true
                    CancellationToken.None

            WriterHandoffOwnerChecks.verifySettlementSignature
                connection
                transaction
                value.CheckpointSigningKeyId
                canonical
                signature
        }

    let preflight
        (connection: NpgsqlConnection)
        transaction
        dataSource
        (witness: WitnessProtocol)
        (verifier: IWriterHandoffEvidenceVerifier)
        commitments
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffSettlement)
        canonical
        signature
        newCapability
        =
        task {
            let! now = ManagedCopySignerPolicy.databaseNow connection transaction
            let snapshot = witness.Snapshot()
            let! evidence = verifier.VerifySettlement(value, canonical, CancellationToken.None)

            if
                not (pairMatches connection transaction snapshot prepared value newCapability)
                || evidence.IsNone
                || not (qualified value prepared.Value evidence.Value now)
            then
                return false
            else
                use! audit = RuntimeDatabase.openConnectionAsync dataSource

                let! summary =
                    DataAudit.runWithSuppression audit witness commitments CancellationToken.None

                if
                    summary.PendingIntents <> 1L
                    || summary.WitnessCutoff <> prepared.Intent.Sequence
                    || summary.WriterHandoffPreparations < 1L
                then
                    return false
                else
                    do!
                        verifySignatures
                            connection
                            transaction
                            witness
                            prepared
                            value
                            canonical
                            signature
                            now

                    return true
        }

    let verifyAfterCommit dataSource (witness: WitnessProtocol) commitments =
        task {
            use! audit = RuntimeDatabase.openConnectionAsync dataSource

            let! summary =
                DataAudit.runWithSuppression audit witness commitments CancellationToken.None

            return summary.PendingIntents = 0L && summary.WriterHandoffs >= 1L
        }
