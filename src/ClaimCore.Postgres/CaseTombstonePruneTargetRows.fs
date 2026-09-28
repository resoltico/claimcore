namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

[<NoEquality; NoComparison>]
type internal StoredWitnessPruneTarget =
    {
        PruneEventId: Guid
        Target: WitnessPruneTarget
    }

module internal CaseTombstonePruneTargetRows =
    let private phase =
        function
        | "INTENT" -> Intent
        | "SETTLED_ACCEPTED" -> SettledAccepted
        | "SETTLED_REVOKED" -> SettledRevoked
        | "SETTLED_AUTHORITY" -> SettledAuthority
        | "ABORTED_BEFORE_COMMIT" -> AbortedBeforeCommit
        | _ -> corrupt ()

    let page (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) caseId after =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT prune_event_id,sequence,operation_id,phase,witness_epoch,"
                    + "entry_hash,payload_sha256,is_external_publication "
                    + "FROM claimcore.case_erasure_prune_targets "
                    + "WHERE case_id=@case AND sequence>@after ORDER BY sequence LIMIT 50",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            Sql.integer command "after" after
            use! reader = command.ExecuteReaderAsync()
            let rows = ResizeArray<StoredWitnessPruneTarget>()

            while reader.Read() do
                rows.Add
                    {
                        PruneEventId = reader.GetGuid(0)
                        Target =
                            {
                                Sequence = reader.GetInt64(1)
                                OperationId = reader.GetGuid(2)
                                Phase = phase (reader.GetString(3))
                                Epoch = reader.GetInt64(4)
                                EntryHash = reader.GetFieldValue<byte array>(5)
                                PayloadHash = reader.GetFieldValue<byte array>(6)
                                IsExternalPublication = reader.GetBoolean(7)
                            }
                    }

            return rows |> Seq.toList
        }
