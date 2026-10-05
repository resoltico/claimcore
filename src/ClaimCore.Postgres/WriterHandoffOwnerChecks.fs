namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.Threading
open Npgsql
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type private OwnerHandoffApproval =
    {
        ApprovalId: Guid
        HandoffId: Guid
        OldGeneration: int64
        ReviewedSequence: int64
        ReviewedHash: byte array
        NewCapabilitySha256: byte array
        CheckpointSigningKeyId: Guid
        FenceReportSha256: byte array
        InventorySha256: byte array
        ActorId: Guid
        ExpiresAt: DateTimeOffset
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
        Candidate: byte array
        Human: bool
        Enabled: bool
        CurrentOwner: bool
        AlreadyUsed: bool
    }

/// Rechecks both currently authorized human owners and independent checkpoint holder.
module internal WriterHandoffOwnerChecks =
    let private query =
        "SELECT a.approval_id,a.handoff_id,a.old_generation,a.expected_witness_sequence,"
        + "a.expected_witness_hash,a.new_capability_sha256,a.checkpoint_signing_key_id,"
        + "a.fence_report_sha256,a.inventory_sha256,a.approver_actor_id,a.expires_at,"
        + "a.witness_sequence,a.witness_epoch,a.witness_entry_hash,a.candidate_sha256,"
        + "actor.principal_kind='HUMAN',actor.enabled,"
        + "EXISTS (SELECT 1 FROM claimcore.actor_grants g WHERE g.actor_id=a.approver_actor_id "
        + "AND g.scope_kind='INSTALLATION' "
        + "AND g.scope_case_id='00000000-0000-0000-0000-000000000000'::uuid "
        + "AND g.role_name='OWNER' AND g.active),"
        + "EXISTS (SELECT 1 FROM claimcore.writer_handoff_approval_uses u "
        + "WHERE u.approval_id=a.approval_id) "
        + "FROM claimcore.writer_handoff_approvals a "
        + "JOIN claimcore.actors actor ON actor.actor_id=a.approver_actor_id "
        + "WHERE a.approval_id IN (@first,@second) ORDER BY a.approval_id"

    let private read (reader: DbDataReader) =
        {
            ApprovalId = reader.GetGuid(0)
            HandoffId = reader.GetGuid(1)
            OldGeneration = reader.GetInt64(2)
            ReviewedSequence = reader.GetInt64(3)
            ReviewedHash = reader.GetFieldValue<byte array>(4)
            NewCapabilitySha256 = reader.GetFieldValue<byte array>(5)
            CheckpointSigningKeyId = reader.GetGuid(6)
            FenceReportSha256 = reader.GetFieldValue<byte array>(7)
            InventorySha256 = reader.GetFieldValue<byte array>(8)
            ActorId = reader.GetGuid(9)
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(10)
            WitnessSequence = reader.GetInt64(11)
            WitnessEpoch = reader.GetInt64(12)
            WitnessHash = reader.GetFieldValue<byte array>(13)
            Candidate = reader.GetFieldValue<byte array>(14)
            Human = reader.GetBoolean(15)
            Enabled = reader.GetBoolean(16)
            CurrentOwner = reader.GetBoolean(17)
            AlreadyUsed = reader.GetBoolean(18)
        }

    let private signer connection transaction keyId =
        use command =
            new NpgsqlCommand(
                "SELECT s.ed25519_public_key,s.holder_actor_id,s.active,"
                + "s.signer_purpose,a.principal_kind,a.enabled,"
                + "EXISTS (SELECT 1 FROM claimcore.actor_grants g "
                + "WHERE g.actor_id=s.holder_actor_id AND g.active "
                + "AND g.scope_kind='INSTALLATION' "
                + "AND g.scope_case_id='00000000-0000-0000-0000-000000000000'::uuid "
                + "AND g.role_name IN ('AUDITOR_CUSTODIAN','DATA_STEWARD')) "
                + "FROM claimcore.managed_copy_signers s "
                + "JOIN claimcore.actors a ON a.actor_id=s.holder_actor_id "
                + "WHERE s.signing_key_id=@key",
                connection,
                transaction
            )

        Sql.uuid command "key" keyId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Writer checkpoint signer is unavailable."

        let publicKey = reader.GetFieldValue<byte array>(0)
        let holder = reader.GetGuid(1)
        let active = reader.GetBoolean(2)
        let purpose = reader.GetString(3)
        let human = reader.GetString(4) = "HUMAN"
        let enabled = reader.GetBoolean(5)
        let granted = reader.GetBoolean(6)

        if
            reader.Read()
            || publicKey.Length <> 32
            || not active
            || purpose <> "CHECKPOINT"
            || not human
            || not enabled
            || not granted
        then
            invalidOp "Writer checkpoint signer is unavailable."

        publicKey, holder

    let private matching
        (value: WriterHandoffPreparation)
        now
        requireUsed
        (approval: OwnerHandoffApproval)
        =
        approval.HandoffId = value.HandoffId
        && approval.OldGeneration = value.OldGeneration
        && approval.ReviewedSequence = value.ReviewedCutoffSequence
        && approval.ReviewedHash = value.ReviewedCutoffHash
        && approval.NewCapabilitySha256 = value.NewCapabilitySha256
        && approval.CheckpointSigningKeyId = value.CheckpointSigningKeyId
        && approval.FenceReportSha256 = value.FenceReportSha256
        && approval.InventorySha256 = value.InventorySha256
        && approval.ExpiresAt > now
        && approval.Human
        && approval.Enabled
        && approval.CurrentOwner
        && approval.AlreadyUsed = requireUsed

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (value: WriterHandoffPreparation)
        (canonical: byte array)
        (signature: byte array)
        now
        requireUsed
        (ct: CancellationToken)
        =
        task {
            use command = new NpgsqlCommand(query, connection, transaction)
            Sql.uuid command "first" value.ApprovalOneId
            Sql.uuid command "second" value.ApprovalTwoId
            use! reader = command.ExecuteReaderAsync(ct)
            let approvals = ResizeArray<OwnerHandoffApproval>()

            while reader.Read() do
                approvals.Add(read reader)

            reader.Close()

            if
                approvals.Count <> 2
                || approvals[0].ActorId = approvals[1].ActorId
                || not (approvals |> Seq.exists (fun row -> row.ApprovalId = value.ApprovalOneId))
                || not (approvals |> Seq.exists (fun row -> row.ApprovalId = value.ApprovalTwoId))
                || not (approvals |> Seq.forall (matching value now requireUsed))
            then
                invalidOp "Two current owner handoff approvals are unavailable."

            let publicKey, holder = signer connection transaction value.CheckpointSigningKeyId

            if
                approvals |> Seq.exists (fun row -> row.ActorId = holder)
                || signature.Length <> 64
                || not (ManagedCopySignature.verify publicKey canonical signature)
            then
                invalidOp "Independent checkpoint signature is unavailable."

            for row in approvals do
                do!
                    witness.VerifyAuthorityEvidenceForInstallation(
                        row.ApprovalId,
                        row.WitnessSequence,
                        row.WitnessEpoch,
                        row.WitnessHash,
                        row.Candidate,
                        ct
                    )
        }

    let verifySettlementSignature connection transaction keyId canonical (signature: byte array) =
        let publicKey, _ = signer connection transaction keyId

        if
            signature.Length <> 64
            || not (ManagedCopySignature.verify publicKey canonical signature)
        then
            invalidOp "Independent checkpoint settlement signature is unavailable."

    let verifyActivationSignatures
        connection
        transaction
        keyId
        holder
        fence
        fenceSignature
        supplement
        supplementSignature
        =
        let publicKey, registeredHolder = signer connection transaction keyId

        if
            registeredHolder <> holder
            || not (ManagedCopySignature.verify publicKey fence fenceSignature)
            || not (ManagedCopySignature.verify publicKey supplement supplementSignature)
        then
            invalidOp "Independent checkpoint activation signatures are unavailable."

    let verifyHistoricalActivationSignatures
        connection
        transaction
        keyId
        holder
        fence
        fenceSignature
        supplement
        supplementSignature
        =
        use command =
            new NpgsqlCommand(
                "SELECT ed25519_public_key,holder_actor_id,signer_purpose "
                + "FROM claimcore.managed_copy_signers WHERE signing_key_id=@key",
                connection,
                transaction
            )

        Sql.uuid command "key" keyId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Historical checkpoint signer is unavailable."

        let publicKey = reader.GetFieldValue<byte array>(0)

        let matching =
            publicKey.Length = 32
            && reader.GetGuid(1) = holder
            && reader.GetString(2) = "CHECKPOINT"
            && ManagedCopySignature.verify publicKey fence fenceSignature
            && ManagedCopySignature.verify publicKey supplement supplementSignature

        if reader.Read() || not matching then
            invalidOp "Historical checkpoint signatures diverged."

    let historicalCheckpointKey connection transaction keyId =
        use command =
            new NpgsqlCommand(
                "SELECT s.ed25519_public_key,s.signer_purpose,"
                + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
                + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=1),"
                + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
                + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=2) "
                + "FROM claimcore.managed_copy_signers s WHERE s.signing_key_id=@key",
                connection,
                transaction
            )

        Sql.uuid command "key" keyId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Historical checkpoint key is missing."

        let publicKey = reader.GetFieldValue<byte array>(0)
        let purpose = reader.GetString(1)
        let registered = reader.GetInt64(2)

        let retired =
            if reader.IsDBNull(3) then
                None
            else
                Some(reader.GetInt64(3))

        if reader.Read() || publicKey.Length <> 32 || purpose <> "CHECKPOINT" then
            invalidOp "Historical checkpoint key diverged."

        publicKey, registered, retired
