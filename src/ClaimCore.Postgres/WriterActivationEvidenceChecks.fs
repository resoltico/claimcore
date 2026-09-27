namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal PrimaryActivationState =
    {
        Generation: int64
        Pending: bool
        ActivationId: Guid option
        ActivationSequence: int64 option
        ActivationHash: byte array option
    }

/// Rechecks signed tail identity against the current owner-held signer and exact W1 pair.
module internal WriterActivationEvidenceChecks =
    let qualified (value: WriterActivationEvidence) (proof: WriterActivationQualification) now =
        proof.HandoffId = value.HandoffId
        && proof.InstallationId = value.InstallationId
        && proof.LineageId = value.LineageId
        && proof.Epoch = value.Epoch
        && proof.WriterGeneration = value.WriterGeneration
        && proof.W1Sequence = value.W1Sequence
        && proof.W1Hash = value.W1Hash
        && proof.PublicationManifestSha256 = value.PublicationManifestSha256
        && proof.ReportSha256 = value.ReportSha256
        && proof.FenceSha256 = value.FenceSha256
        && proof.SupplementSha256 = value.SupplementSha256
        && proof.FinalWalObjectSha256 = value.FinalWalObjectSha256
        && proof.FinalWalObjectCount = value.FinalWalObjectCount
        && proof.IndependentProbeSha256 = value.IndependentProbeSha256
        && proof.ProbeEvidenceSha256 = value.ProbeEvidenceSha256
        && proof.CheckpointSigningKeyId = value.CheckpointSigningKeyId
        && proof.CheckpointHolderActorId = value.CheckpointHolderActorId
        && proof.ValidUntil > now
        && proof.ValidUntil <= value.ValidUntil

    let private digestMatches (source: byte array) (expected: byte array) =
        not (isNull (box source) || isNull (box expected))
        && expected.Length = 32
        && CryptographicOperations.FixedTimeEquals(
            ReadOnlySpan<byte>(SHA256.HashData(source)),
            ReadOnlySpan<byte>(expected)
        )

    let private shapes (value: WriterActivationEvidence) =
        value.HandoffId <> Guid.Empty
        && value.InstallationId <> Guid.Empty
        && value.LineageId <> Guid.Empty
        && value.Epoch > 0L
        && value.WriterGeneration > 1L
        && value.W1Sequence > 0L
        && value.CheckpointSigningKeyId <> Guid.Empty
        && value.CheckpointHolderActorId <> Guid.Empty
        && value.FinalWalObjectCount >= 2
        && value.FinalWalObjectCount <= 2000
        && value.ReportSignature.Length = 64
        && value.FenceSignature.Length = 64
        && value.SupplementSignature.Length = 64
        && value.SignedReport.Length > 0
        && value.SignedReport.Length <= 4194304
        && value.SignedFence.Length > 0
        && value.SignedFence.Length <= 65536
        && value.SignedSupplement.Length > 0
        && value.SignedSupplement.Length <= 4194304
        && value.PublicationManifestSha256.Length = 32
        && digestMatches value.SignedReport value.ReportSha256
        && digestMatches value.SignedFence value.FenceSha256
        && digestMatches value.SignedSupplement value.SupplementSha256
        && value.FinalWalObjectSha256.Length = 32
        && value.IndependentProbeSha256.Length = 32
        && value.ProbeEvidenceSha256.Length = 32
        && value.W1Hash.Length = 32

    let private primary
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterActivationEvidence)
        =
        use command =
            new NpgsqlCommand(
                "SELECT l.installation_id,l.lineage_id,l.witness_epoch,l.writer_generation,"
                + "l.writer_handoff_event_id,l.writer_handoff_sequence,l.writer_handoff_hash,"
                + "l.writer_activation_pending,l.writer_activation_event_id,"
                + "l.writer_activation_sequence,l.writer_activation_hash,"
                + "h.new_generation,h.settlement_sequence,h.settlement_hash,h.checkpoint_signing_key_id "
                + "FROM claimcore.installation_lineage l JOIN claimcore.writer_handoffs h "
                + "ON h.handoff_id=@handoff WHERE l.singleton FOR UPDATE OF l,h",
                connection,
                transaction
            )

        Sql.uuid command "handoff" value.HandoffId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Writer activation W1 primary evidence is absent."

        let optional index read =
            if reader.IsDBNull(index) then None else Some(read index)

        let state =
            {
                Generation = reader.GetInt64(3)
                Pending = reader.GetBoolean(7)
                ActivationId = optional 8 reader.GetGuid
                ActivationSequence = optional 9 reader.GetInt64
                ActivationHash = optional 10 reader.GetFieldValue<byte array>
            }

        let matching =
            reader.GetGuid(0) = value.InstallationId
            && reader.GetGuid(1) = value.LineageId
            && reader.GetInt64(2) = value.Epoch
            && state.Generation = value.WriterGeneration
            && reader.GetGuid(4) = value.HandoffId
            && reader.GetInt64(5) = value.W1Sequence
            && reader.GetFieldValue<byte array>(6) = value.W1Hash
            && reader.GetInt64(11) = value.WriterGeneration
            && reader.GetInt64(12) = value.W1Sequence
            && reader.GetFieldValue<byte array>(13) = value.W1Hash
            && reader.GetGuid(14) = value.CheckpointSigningKeyId

        if reader.Read() || not matching then
            invalidOp "Writer activation W1 primary evidence diverged."

        state

    let private verifyCore
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (value: WriterActivationEvidence)
        requireCurrentProof
        now
        =
        if
            not (shapes value)
            || (requireCurrentProof && value.ValidUntil <= now)
            || value.InstallationId <> witness.Identity.InstallationId
            || value.LineageId <> witness.Identity.LineageId
            || value.Epoch <> witness.Identity.Epoch
        then
            invalidOp "Writer activation evidence is invalid."

        let state = primary connection transaction value

        let verifySignatures =
            if requireCurrentProof then
                WriterHandoffOwnerChecks.verifyActivationSignatures
            else
                WriterHandoffOwnerChecks.verifyHistoricalActivationSignatures

        verifySignatures
            connection
            transaction
            value.CheckpointSigningKeyId
            value.CheckpointHolderActorId
            value.SignedFence
            value.FenceSignature
            value.SignedSupplement
            value.SupplementSignature

        WitnessProtocolHandoff.verifySettlement
            witness
            value.HandoffId
            value.W1Sequence
            value.W1Hash

        let snapshot = witness.Snapshot()

        if snapshot.WriterGeneration <> value.WriterGeneration || snapshot.HandoffPending then
            invalidOp "Writer activation witness generation diverged."

        state, snapshot

    let verify connection transaction witness value now =
        verifyCore connection transaction witness value true now

    let verifyHistorical connection transaction witness value =
        verifyCore connection transaction witness value false DateTimeOffset.MinValue
