namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application

[<NoEquality; NoComparison>]
type internal TerminalApprovalAuditRow =
    {
        ApprovalId: Guid
        Proposal: TombstoneTerminalProposal
        ActorId: Guid
        GrantRevision: int64
        ApprovedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        Canonical: byte array
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
    }

module internal CaseTombstoneTerminalApprovalAuditRows =
    let private sql =
        "SELECT approval_id,terminal_event_id,case_id,action_name,prune_event_id,"
        + "expected_authority_revision,expected_authority_hash,installation_id,lineage_id,"
        + "witness_epoch,witness_cutoff_sequence,witness_cutoff_hash,copy_inventory_digest,"
        + "relevant_copy_count,expected_writer_generation,recovery_fence_digest,"
        + "old_writer_generation,new_writer_generation,policy_id,suppression_until,valid_until,"
        + "approver_actor_id,approver_grant_revision,approved_at,expires_at,canonical_action,"
        + "candidate_sha256,witness_sequence,approval_witness_epoch,witness_entry_hash "
        + "FROM claimcore.case_erasure_terminal_approvals WHERE case_id=@case "
        + "AND approval_id>@after ORDER BY approval_id LIMIT 50"

    let private common (reader: Data.Common.DbDataReader) =
        {
            EventId = reader.GetGuid(1)
            CaseId = reader.GetGuid(2)
            ExpectedAuthorityRevision = reader.GetInt64(5)
            ExpectedAuthorityHash = Convert.ToHexStringLower(reader.GetFieldValue<byte array>(6))
            InstallationId = reader.GetGuid(7)
            LineageId = reader.GetGuid(8)
            WitnessEpoch = reader.GetInt64(9)
            PruneEventId = reader.GetGuid(4)
            WitnessCutoffSequence = reader.GetInt64(10)
            WitnessCutoffHash = Convert.ToHexStringLower(reader.GetFieldValue<byte array>(11))
            CopyInventoryDigest = Convert.ToHexStringLower(reader.GetFieldValue<byte array>(12))
            RelevantCopyCount = reader.GetInt64(13)
            ExpectedWriterGeneration = reader.GetInt64(14)
            PolicyId = reader.GetString(18)
            SuppressionUntil = reader.GetFieldValue<DateTimeOffset>(19)
            ValidUntil = reader.GetFieldValue<DateTimeOffset>(20)
        }

    let private proposal (reader: Data.Common.DbDataReader) =
        let copy = common reader

        match reader.GetString(3) with
        | "CONFIRM_MANAGED_PAYLOAD_ABSENCE" when
            reader.IsDBNull(15) && reader.IsDBNull(16) && reader.IsDBNull(17)
            ->
            TombstoneTerminalProposal.ConfirmManagedPayloadAbsence copy
        | "COMPLETE_SUPPRESSION_HORIZON" when
            not (reader.IsDBNull(15) || reader.IsDBNull(16) || reader.IsDBNull(17))
            ->
            TombstoneTerminalProposal.CompleteSuppressionHorizon
                {
                    Copy = copy
                    RecoveryFenceDigest =
                        Convert.ToHexStringLower(reader.GetFieldValue<byte array>(15))
                    OldWriterGeneration = reader.GetInt64(16)
                    NewWriterGeneration = reader.GetInt64(17)
                }
        | _ -> invalidOp "Terminal approval action projection is invalid."

    let private row (reader: Data.Common.DbDataReader) =
        {
            ApprovalId = reader.GetGuid(0)
            Proposal = proposal reader
            ActorId = reader.GetGuid(21)
            GrantRevision = reader.GetInt64(22)
            ApprovedAt = reader.GetFieldValue<DateTimeOffset>(23)
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(24)
            Canonical = reader.GetFieldValue<byte array>(25)
            CandidateHash = reader.GetFieldValue<byte array>(26)
            WitnessSequence = reader.GetInt64(27)
            WitnessEpoch = reader.GetInt64(28)
            WitnessHash = reader.GetFieldValue<byte array>(29)
        }

    let page (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) caseId after =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            Sql.uuid command "case" caseId
            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync()
            let rows = ResizeArray<TerminalApprovalAuditRow>()

            while reader.Read() do
                rows.Add(row reader)

            return rows |> Seq.toList
        }
