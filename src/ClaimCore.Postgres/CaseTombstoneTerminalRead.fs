namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql
open ClaimCore.Domain

[<NoEquality; NoComparison>]
type internal StoredTerminalTombstone =
    {
        CaseId: Guid
        Phase: string
        PruneEventId: Guid
        CutoffSequence: int64
        CutoffHash: byte array
        AuthorityRevision: int64
        AuthorityHash: byte array
        InstallationId: Guid
        LineageId: Guid
        WitnessEpoch: int64
        WriterGeneration: int64
        SourceRevision: int64
        Disposition: CaseDisposition
        ReferenceCommitment: byte array
        CopyAbsenceEventId: Guid option
        SuppressionFinalEventId: Guid option
        RetentionPolicyId: string option
        SuppressionUntil: DateTimeOffset option
    }

[<NoEquality; NoComparison>]
type internal StoredTerminalApproval =
    {
        CaseId: Guid
        TerminalEventId: Guid
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

module internal CaseTombstoneTerminalRead =
    let private tombstone (reader: System.Data.Common.DbDataReader) =
        {
            CaseId = reader.GetGuid(0)
            Phase = reader.GetString(1)
            PruneEventId = reader.GetGuid(2)
            CutoffSequence = reader.GetInt64(3)
            CutoffHash = reader.GetFieldValue<byte array>(4)
            AuthorityRevision = reader.GetInt64(5)
            AuthorityHash = reader.GetFieldValue<byte array>(6)
            InstallationId = reader.GetGuid(7)
            LineageId = reader.GetGuid(8)
            WitnessEpoch = reader.GetInt64(9)
            WriterGeneration = reader.GetInt64(10)
            SourceRevision = reader.GetInt64(11)
            Disposition =
                CaseLifecycleCandidate.parseDisposition (reader.GetString(12))
                |> Option.defaultWith (fun () ->
                    raise (InvalidDataException("Terminal disposition is invalid.")))
            ReferenceCommitment = reader.GetFieldValue<byte array>(13)
            CopyAbsenceEventId =
                if reader.IsDBNull(14) then
                    None
                else
                    Some(reader.GetGuid(14))
            SuppressionFinalEventId =
                if reader.IsDBNull(15) then
                    None
                else
                    Some(reader.GetGuid(15))
            RetentionPolicyId =
                if reader.IsDBNull(16) then
                    None
                else
                    Some(reader.GetString(16))
            SuppressionUntil =
                if reader.IsDBNull(17) then
                    None
                else
                    Some(reader.GetFieldValue<DateTimeOffset>(17))
        }

    let lock (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) caseId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT t.case_id,t.phase,t.witness_prune_event_id,"
                    + "t.witness_prune_cutoff_sequence,t.witness_prune_cutoff_hash,"
                    + "a.revision,a.event_hash,i.installation_id,i.lineage_id,"
                    + "i.witness_epoch,i.writer_generation,t.source_revision,"
                    + "t.source_disposition,t.reference_commitment,t.copy_absence_event_id,"
                    + "t.suppression_final_event_id,t.retention_policy_id,t.suppression_until "
                    + "FROM claimcore.case_erasure_tombstones t "
                    + "JOIN claimcore.case_erasure_authority_tip a ON a.case_id=t.case_id "
                    + "CROSS JOIN claimcore.installation_lineage i "
                    + "WHERE t.case_id=@case AND t.phase IN "
                    + "('ERASURE_PENDING','PAYLOAD_ERASED_SUPPRESSION_RETAINED') "
                    + "AND t.witness_prune_event_id IS NOT NULL AND i.singleton "
                    + "FOR UPDATE OF a",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                return None
            else
                let value = tombstone reader

                if reader.Read() then
                    raise (InvalidDataException("Terminal erasure tombstone is duplicated."))

                return Some value
        }

    let findApproval connection transaction approvalId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT case_id,terminal_event_id,approver_actor_id,"
                    + "approver_grant_revision,approved_at,expires_at,canonical_action,"
                    + "candidate_sha256,witness_sequence,approval_witness_epoch,witness_entry_hash "
                    + "FROM claimcore.case_erasure_terminal_approvals WHERE approval_id=@approval",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                return None
            else
                let value =
                    {
                        CaseId = reader.GetGuid(0)
                        TerminalEventId = reader.GetGuid(1)
                        ActorId = reader.GetGuid(2)
                        GrantRevision = reader.GetInt64(3)
                        ApprovedAt = reader.GetFieldValue<DateTimeOffset>(4)
                        ExpiresAt = reader.GetFieldValue<DateTimeOffset>(5)
                        Canonical = reader.GetFieldValue<byte array>(6)
                        CandidateHash = reader.GetFieldValue<byte array>(7)
                        WitnessSequence = reader.GetInt64(8)
                        WitnessEpoch = reader.GetInt64(9)
                        WitnessHash = reader.GetFieldValue<byte array>(10)
                    }

                if reader.Read() then
                    raise (InvalidDataException("Terminal erasure approval is duplicated."))

                return Some value
        }

    let approvers connection transaction eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT approver_actor_id FROM claimcore.case_erasure_terminal_approvals "
                    + "WHERE terminal_event_id=@event ORDER BY approver_actor_id LIMIT 3",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            use! reader = command.ExecuteReaderAsync()
            let actors = ResizeArray<Guid>()

            while reader.Read() do
                actors.Add(reader.GetGuid(0))

            return actors |> Seq.toList
        }
