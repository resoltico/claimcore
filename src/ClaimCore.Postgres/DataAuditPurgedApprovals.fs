namespace ClaimCore.Postgres

open System
open Npgsql
open NpgsqlTypes
open DataAuditCommon

/// Minimal pseudonymous approval receipts remain after the raw draft and approval bytes are
/// deleted. The later purge event binds these commitments to the independently witnessed act.
module internal DataAuditPurgedApprovals =
    let private row (reader: Data.Common.DbDataReader) : ErasurePurgeApprovalReceipt =
        {
            ApprovalId = reader.GetGuid(0)
            ApproverId = reader.GetGuid(1)
            GrantRevision = reader.GetInt64(2)
            ApprovedAt = reader.GetFieldValue<DateTimeOffset>(3)
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(4)
            DraftSha256 = Array.empty
            DraftCommitment = reader.GetFieldValue<byte array>(5)
            Canonical = Array.empty
            CandidateSha256 = Array.empty
            ApprovalCommitment = reader.GetFieldValue<byte array>(6)
            WitnessSequence = reader.GetInt64(7)
            WitnessEpoch = reader.GetInt64(8)
            WitnessHash = reader.GetFieldValue<byte array>(9)
        }

    let read connection transaction caseId purgeEventId cutoff purgedAt =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT a.approval_id,a.approver_actor_id,a.approver_grant_revision,"
                    + "a.approved_at,a.expires_at,a.draft_commitment,a.approval_commitment,"
                    + "a.witness_sequence,a.witness_epoch,a.witness_entry_hash,actor.principal_kind "
                    + "FROM claimcore.case_erasure_purge_approvals a "
                    + "JOIN claimcore.actors actor ON actor.actor_id=a.approver_actor_id "
                    + "WHERE a.case_id=@case AND a.purge_event_id=@event "
                    + "ORDER BY a.approval_id LIMIT 3",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            Sql.uuid command "event" purgeEventId
            use! reader = command.ExecuteReaderAsync()
            let approvals = ResizeArray<ErasurePurgeApprovalReceipt>()

            while reader.Read() do
                let approval = row reader

                if
                    reader.GetString(10) <> "HUMAN"
                    || approval.GrantRevision < 1L
                    || approval.ApprovedAt.Offset <> TimeSpan.Zero
                    || approval.ExpiresAt.Offset <> TimeSpan.Zero
                    || approval.ApprovedAt > purgedAt
                    || approval.ExpiresAt <= purgedAt
                    || approval.ExpiresAt - approval.ApprovedAt > TimeSpan.FromHours 24.0
                    || approval.WitnessSequence > cutoff
                    || approval.DraftCommitment.Length <> 32
                    || approval.ApprovalCommitment.Length <> 32
                then
                    corrupt ()

                approvals.Add(approval)

            if approvals.Count <> 2 || approvals[0].ApproverId = approvals[1].ApproverId then
                corrupt ()

            return approvals |> Seq.toList
        }
