namespace ClaimCore.Postgres

open System
open Npgsql

[<NoEquality; NoComparison>]
type internal TombstonePruneApprovalAuditRow =
    {
        ApprovalId: Guid
        CaseId: Guid
        PruneEventId: Guid
        PurgeEventId: Guid
        CutoffSequence: int64
        CutoffHash: byte array
        TargetCount: int64
        TargetDigest: byte array
        AuthorityRevision: int64
        AuthorityHash: byte array
        ValidUntil: DateTimeOffset
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

module internal CaseTombstonePruneApprovalAuditRows =
    let private sql =
        "SELECT approval_id,case_id,prune_event_id,purge_event_id,cutoff_sequence,cutoff_hash,"
        + "target_count,target_digest,expected_authority_revision,expected_authority_hash,"
        + "valid_until,approver_actor_id,approver_grant_revision,approved_at,expires_at,"
        + "canonical_action,candidate_sha256,witness_sequence,witness_epoch,witness_entry_hash "
        + "FROM claimcore.case_erasure_prune_approvals WHERE case_id=@case "
        + "AND approval_id>@after ORDER BY approval_id LIMIT 50"

    let page (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) caseId after =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            Sql.uuid command "case" caseId
            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync()
            let rows = ResizeArray<TombstonePruneApprovalAuditRow>()

            while reader.Read() do
                rows.Add
                    {
                        ApprovalId = reader.GetGuid(0)
                        CaseId = reader.GetGuid(1)
                        PruneEventId = reader.GetGuid(2)
                        PurgeEventId = reader.GetGuid(3)
                        CutoffSequence = reader.GetInt64(4)
                        CutoffHash = reader.GetFieldValue<byte array>(5)
                        TargetCount = reader.GetInt64(6)
                        TargetDigest = reader.GetFieldValue<byte array>(7)
                        AuthorityRevision = reader.GetInt64(8)
                        AuthorityHash = reader.GetFieldValue<byte array>(9)
                        ValidUntil = reader.GetFieldValue<DateTimeOffset>(10)
                        ActorId = reader.GetGuid(11)
                        GrantRevision = reader.GetInt64(12)
                        ApprovedAt = reader.GetFieldValue<DateTimeOffset>(13)
                        ExpiresAt = reader.GetFieldValue<DateTimeOffset>(14)
                        Canonical = reader.GetFieldValue<byte array>(15)
                        CandidateHash = reader.GetFieldValue<byte array>(16)
                        WitnessSequence = reader.GetInt64(17)
                        WitnessEpoch = reader.GetInt64(18)
                        WitnessHash = reader.GetFieldValue<byte array>(19)
                    }

            return rows |> Seq.toList
        }
