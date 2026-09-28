namespace ClaimCore.Postgres

open System
open Npgsql

[<NoEquality; NoComparison>]
type internal StoredAdoptionApproval =
    {
        CaseId: Guid
        AdoptionEventId: Guid
        CopyId: Guid
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

module internal ManagedCopyAdoptionApprovalRead =
    let find connection transaction approvalId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT case_id,adoption_event_id,copy_id,owner_actor_id,"
                    + "owner_grant_revision,approved_at,expires_at,canonical_action,"
                    + "candidate_sha256,witness_sequence,witness_epoch,witness_entry_hash "
                    + "FROM claimcore.managed_copy_adoption_approvals WHERE approval_id=@approval",
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
                        AdoptionEventId = reader.GetGuid(1)
                        CopyId = reader.GetGuid(2)
                        ActorId = reader.GetGuid(3)
                        GrantRevision = reader.GetInt64(4)
                        ApprovedAt = reader.GetFieldValue<DateTimeOffset>(5)
                        ExpiresAt = reader.GetFieldValue<DateTimeOffset>(6)
                        Canonical = reader.GetFieldValue<byte array>(7)
                        CandidateHash = reader.GetFieldValue<byte array>(8)
                        WitnessSequence = reader.GetInt64(9)
                        WitnessEpoch = reader.GetInt64(10)
                        WitnessHash = reader.GetFieldValue<byte array>(11)
                    }

                return if reader.Read() then None else Some value
        }

    let eventAvailable connection transaction eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT NOT EXISTS(SELECT 1 FROM claimcore.managed_copy_adoption_approvals "
                    + "WHERE adoption_event_id=@event)",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            let! value = command.ExecuteScalarAsync()
            return unbox<bool> value
        }
