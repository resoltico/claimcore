namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open DataAuditCommon
open WitnessProtocolReconciliation

[<NoEquality; NoComparison>]
type private WriterHandoffApprovalAuditRow =
    {
        Request: WriterHandoffApprovalRequest
        ActorId: Guid
        GrantRevision: int64
        Canonical: byte array
        Candidate: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
        ActorKind: string
        SignerPurpose: string
        SignerHolder: Guid
        RegisteredSequence: int64
        RetiredSequence: int64 option
        UsedHandoffId: Guid option
    }

/// Replays every owner approval, historical grant, signer custody and one-use handoff link.
module internal DataAuditWriterHandoffApprovals =
    let private query =
        "SELECT a.approval_id,a.handoff_id,a.old_generation,a.expected_witness_sequence,"
        + "a.expected_witness_hash,a.new_capability_sha256,a.checkpoint_signing_key_id,"
        + "a.fence_report_sha256,a.inventory_sha256,a.approver_actor_id,"
        + "a.approver_grant_revision,a.expires_at,a.canonical_action,a.candidate_sha256,"
        + "a.witness_sequence,a.witness_epoch,a.witness_entry_hash,actor.principal_kind,"
        + "s.signer_purpose,s.holder_actor_id,"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=1),"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=2),u.handoff_id "
        + "FROM claimcore.writer_handoff_approvals a "
        + "JOIN claimcore.actors actor ON actor.actor_id=a.approver_actor_id "
        + "JOIN claimcore.managed_copy_signers s ON s.signing_key_id=a.checkpoint_signing_key_id "
        + "LEFT JOIN claimcore.writer_handoff_approval_uses u ON u.approval_id=a.approval_id "
        + "WHERE a.approval_id>@after ORDER BY a.approval_id LIMIT 50"

    let private bytes (reader: NpgsqlDataReader) index = reader.GetFieldValue<byte array>(index)

    let private read (reader: NpgsqlDataReader) =
        {
            Request =
                {
                    ApprovalId = reader.GetGuid(0)
                    HandoffId = reader.GetGuid(1)
                    OldGeneration = reader.GetInt64(2)
                    ExpectedWitnessSequence = reader.GetInt64(3)
                    ExpectedWitnessHash = bytes reader 4
                    NewCapabilitySha256 = bytes reader 5
                    CheckpointSigningKeyId = reader.GetGuid(6)
                    FenceReportSha256 = bytes reader 7
                    InventorySha256 = bytes reader 8
                    ExpiresAt = reader.GetFieldValue<DateTimeOffset>(11)
                }
            ActorId = reader.GetGuid(9)
            GrantRevision = reader.GetInt64(10)
            Canonical = bytes reader 12
            Candidate = bytes reader 13
            WitnessSequence = reader.GetInt64(14)
            WitnessEpoch = reader.GetInt64(15)
            WitnessHash = bytes reader 16
            ActorKind = reader.GetString(17)
            SignerPurpose = reader.GetString(18)
            SignerHolder = reader.GetGuid(19)
            RegisteredSequence = reader.GetInt64(20)
            RetiredSequence =
                if reader.IsDBNull(21) then
                    None
                else
                    Some(reader.GetInt64(21))
            UsedHandoffId =
                if reader.IsDBNull(22) then
                    None
                else
                    Some(reader.GetGuid(22))
        }

    let private page connection transaction after (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(query, connection, transaction)
            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<WriterHandoffApprovalAuditRow>()

            while reader.Read() do
                rows.Add(read reader)

            return rows.ToArray()
        }

    let private verifyOne connection transaction (witness: WitnessProtocol) cutoff row ct =
        task {
            let request = row.Request

            let canonical =
                WriterHandoffApprovalCandidate.canonical request row.ActorId row.GrantRevision

            try
                if
                    row.ActorKind <> "HUMAN"
                    || row.SignerPurpose <> "CHECKPOINT"
                    || row.SignerHolder = row.ActorId
                    || row.GrantRevision < 1L
                    || row.WitnessSequence <= row.RegisteredSequence
                    || row.WitnessSequence > cutoff
                    || row.WitnessEpoch <> witness.Identity.Epoch
                    || row.RetiredSequence
                       |> Option.exists (fun value -> row.WitnessSequence >= value)
                    || row.Canonical <> canonical
                    || row.Candidate <> SHA256.HashData(canonical)
                    || row.UsedHandoffId <> None && row.UsedHandoffId <> Some request.HandoffId
                then
                    corrupt ()

                do!
                    DataAuditHandoffOwnerRole.verify
                        connection
                        transaction
                        row.ActorId
                        row.GrantRevision
                        ct

                do!
                    witnessProofAsync (fun () ->
                        task {
                            do!
                                witness.VerifyHistoricalTip(
                                    request.ExpectedWitnessSequence,
                                    request.ExpectedWitnessHash,
                                    ct
                                )

                            do!
                                witness.VerifyAuthorityEvidenceForInstallation(
                                    request.ApprovalId,
                                    row.WitnessSequence,
                                    row.WitnessEpoch,
                                    row.WitnessHash,
                                    row.Candidate,
                                    ct
                                )
                        })
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

    let verify connection transaction witness cutoff (ct: CancellationToken) =
        task {
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                let! rows = page connection transaction after ct

                for row in rows do
                    if row.Request.ApprovalId <= after then
                        corrupt ()

                    do! verifyOne connection transaction witness cutoff row ct
                    after <- row.Request.ApprovalId
                    count <- count + 1L

                more <- rows.Length = 50

            return count
        }
