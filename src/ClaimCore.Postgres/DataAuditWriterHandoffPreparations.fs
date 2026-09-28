namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon
open WitnessProtocolReconciliation

/// Every primary PREPARE target must match the signed pending-or-settled witness intent.
module internal DataAuditWriterHandoffPreparations =
    let private matches (row: WriterHandoffPreparationAuditRow) (value: WriterHandoffPreparation) =
        row.HandoffId = value.HandoffId
        && row.OldGeneration = value.OldGeneration
        && row.NewGeneration = value.NewGeneration
        && row.CheckpointSigningKeyId = value.CheckpointSigningKeyId
        && row.ApprovalOneId = value.ApprovalOneId
        && row.ApprovalTwoId = value.ApprovalTwoId
        && row.ReviewedCutoffSequence = value.ReviewedCutoffSequence
        && row.ReviewedCutoffHash = value.ReviewedCutoffHash
        && row.PreviousSequence = value.ExpectedTipSequence
        && row.PreviousHash = value.ExpectedTipHash
        && row.NewCapabilitySha256 = value.NewCapabilitySha256
        && row.FenceReportSha256 = value.FenceReportSha256
        && row.InventorySha256 = value.InventorySha256
        && row.RestoreReportSha256 = value.RestoreReportSha256

    let private witnessedMatches
        (row: WriterHandoffPreparationAuditRow)
        (value: WriterHandoffEvidence)
        =
        row.WitnessSequence = value.PrepareSequence
        && row.WitnessHash = value.PrepareHash
        && row.Canonical = value.PrepareCanonical
        && row.Signature = value.PrepareSignature
        && row.Candidate = value.PrepareCandidateSha256

    let private signerMatches (row: WriterHandoffPreparationAuditRow) =
        row.SignerPurpose = "CHECKPOINT"
        && row.SignerPublicKey.Length = 32
        && row.SignerRegisteredSequence < row.WitnessSequence
        && (row.SignerRetiredSequence |> Option.forall (fun n -> n > row.WitnessSequence))
        && row.Candidate = SHA256.HashData(row.Canonical)
        && ManagedCopySignature.verify row.SignerPublicKey row.Canonical row.Signature

    let private identityMatches
        (witness: WitnessProtocol)
        (value: WriterHandoffPreparation)
        (row: WriterHandoffPreparationAuditRow)
        cutoff
        =
        value.InstallationId = witness.Identity.InstallationId
        && value.LineageId = witness.Identity.LineageId
        && value.Epoch = witness.Identity.Epoch
        && row.WitnessEpoch = witness.Identity.Epoch
        && row.WitnessSequence <= cutoff
        && row.WitnessSequence = row.PreviousSequence + 1L

    let private evidence (witness: WitnessProtocol) cutoff (row: WriterHandoffPreparationAuditRow) =
        let value =
            WriterHandoffPreparation.parse row.Canonical |> Option.defaultWith corrupt

        let witnessed =
            witness.EvidenceStore.TryReadHandoff(row.HandoffId)
            |> Option.defaultWith corrupt

        if
            not (matches row value)
            || not (identityMatches witness value row cutoff)
            || not (witnessedMatches row witnessed)
            || not (signerMatches row)
            || value.ValidUntil <= row.RecordedAt
        then
            corrupt ()

        witnessProof (fun () ->
            witness.VerifyHistoricalTip(row.ReviewedCutoffSequence, row.ReviewedCutoffHash)
            witness.VerifyHistoricalTip(row.PreviousSequence, row.PreviousHash)
            witness.VerifyHistoricalTip(row.WitnessSequence, row.WitnessHash))

        let intent =
            witness.EvidenceStore.TryReadEvidence(row.HandoffId, Intent)
            |> Option.defaultWith corrupt

        if
            intent.Ticket.Sequence <> row.WitnessSequence
            || intent.Ticket.EntryHash <> row.WitnessHash
            || intent.Ticket.ScopeKind <> Installation
            || intent.Ticket.SubjectCaseId.IsSome
        then
            corrupt ()

        let plain =
            witness.KeyCustody.Decrypt(
                intent.Ticket.KeyId,
                witness.AssociatedData(row.HandoffId, "INTENT"),
                intent.EncryptedPayload
            )

        try
            if plain <> row.Canonical then
                corrupt ()
        finally
            CryptographicOperations.ZeroMemory(plain)

    let private uses
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (row: WriterHandoffPreparationAuditRow)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT u.approval_id,a.approver_actor_id,a.expires_at,"
                    + "a.old_generation,a.new_capability_sha256,a.checkpoint_signing_key_id "
                    + "FROM claimcore.writer_handoff_approval_uses u "
                    + "JOIN claimcore.writer_handoff_approvals a ON a.approval_id=u.approval_id "
                    + "WHERE u.handoff_id=@handoff ORDER BY u.approval_id",
                    connection,
                    transaction
                )

            Sql.uuid command "handoff" row.HandoffId
            use! reader = command.ExecuteReaderAsync(ct)
            let ids = ResizeArray<Guid>()
            let actors = ResizeArray<Guid>()

            while reader.Read() do
                if
                    reader.GetFieldValue<DateTimeOffset>(2) <= row.RecordedAt
                    || reader.GetInt64(3) <> row.OldGeneration
                    || reader.GetFieldValue<byte array>(4) <> row.NewCapabilitySha256
                    || reader.GetGuid(5) <> row.CheckpointSigningKeyId
                then
                    corrupt ()

                ids.Add(reader.GetGuid(0))
                actors.Add(reader.GetGuid(1))

            if
                ids.Count <> 2
                || not (ids.Contains(row.ApprovalOneId))
                || not (ids.Contains(row.ApprovalTwoId))
                || actors[0] = actors[1]
                || actors.Contains(row.SignerHolder)
            then
                corrupt ()
        }

    let verify connection transaction witness cutoff (ct: CancellationToken) =
        task {
            let mutable after = 0L
            let mutable more = true
            let mutable count = 0L

            while more do
                let! rows =
                    DataAuditWriterHandoffPreparationRows.page connection transaction after ct

                for row in rows do
                    if row.WitnessSequence <= after then
                        corrupt ()

                    evidence witness cutoff row
                    do! uses connection transaction row ct
                    after <- row.WitnessSequence
                    count <- count + 1L

                more <- rows.Length = 50

            return count
        }
