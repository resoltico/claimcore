namespace ClaimCore.Postgres

open System
open Npgsql

[<NoEquality; NoComparison>]
type internal ActorAuthorityAuditRow =
    {
        Revision: int64
        EventId: Guid
        ActionName: string
        TargetActorId: Guid
        ApproverActorId: Guid option
        Canonical: byte array
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
    }

module internal DataAuditAuthorityRows =
    let page
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        afterRevision
        (ct: Threading.CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT revision,event_id,action_name,target_actor_id,approver_actor_id,"
                    + "canonical_action,candidate_sha256,witness_sequence,witness_epoch,"
                    + "witness_entry_hash FROM claimcore.actor_authority_events "
                    + "WHERE revision>@after ORDER BY revision LIMIT 50",
                    connection,
                    transaction
                )

            Sql.integer command "after" afterRevision
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<ActorAuthorityAuditRow>()

            while reader.Read() do
                rows.Add
                    {
                        Revision = reader.GetInt64(0)
                        EventId = reader.GetGuid(1)
                        ActionName = reader.GetString(2)
                        TargetActorId = reader.GetGuid(3)
                        ApproverActorId =
                            if reader.IsDBNull(4) then None else Some(reader.GetGuid(4))
                        Canonical = reader.GetFieldValue<byte array>(5)
                        CandidateHash = reader.GetFieldValue<byte array>(6)
                        WitnessSequence = reader.GetInt64(7)
                        WitnessEpoch = reader.GetInt64(8)
                        WitnessHash = reader.GetFieldValue<byte array>(9)
                    }

            return rows |> Seq.toList
        }
