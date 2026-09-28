namespace ClaimCore.Postgres

open System
open Npgsql

[<NoEquality; NoComparison>]
type internal StoredPruneApproval =
    {
        CaseId: Guid
        PruneEventId: Guid
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

module internal CaseTombstonePruneApprovalRead =
    let find connection transaction approvalId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT case_id,prune_event_id,approver_actor_id,approver_grant_revision,"
                    + "approved_at,expires_at,canonical_action,candidate_sha256,"
                    + "witness_sequence,witness_epoch,witness_entry_hash "
                    + "FROM claimcore.case_erasure_prune_approvals WHERE approval_id=@approval",
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
                        PruneEventId = reader.GetGuid(1)
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
                    invalidOp "Witness prune approval identity is duplicated."

                return Some value
        }

    let approvers connection transaction pruneEventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT approver_actor_id FROM claimcore.case_erasure_prune_approvals "
                    + "WHERE prune_event_id=@event ORDER BY approver_actor_id LIMIT 3",
                    connection,
                    transaction
                )

            Sql.uuid command "event" pruneEventId
            use! reader = command.ExecuteReaderAsync()
            let actors = ResizeArray<Guid>()

            while reader.Read() do
                actors.Add(reader.GetGuid(0))

            return actors |> Seq.toList
        }
