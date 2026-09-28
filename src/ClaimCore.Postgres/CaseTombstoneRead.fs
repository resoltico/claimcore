namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql

[<NoEquality; NoComparison>]
type internal StoredCaseTombstone =
    {
        CaseId: Guid
        Phase: string
        RequestWitnessSequence: int64
        RequestedAt: DateTimeOffset
        PurgeEventId: Guid
        PurgeCanonical: byte array
        PurgeCandidateHash: byte array
        PurgeWitnessSequence: int64
        PurgeWitnessEpoch: int64
        PurgeWitnessHash: byte array
        AuthorityRevision: int64
        AuthorityHash: byte array
        PruneEventId: Guid option
    }

module internal CaseTombstoneRead =
    let private sql =
        "SELECT t.case_id,t.purge_event_id,t.purge_canonical_action,"
        + "t.purge_candidate_sha256,t.purge_witness_sequence,t.purge_witness_epoch,"
        + "t.purge_witness_entry_hash,a.revision,a.event_hash,t.witness_prune_event_id,"
        + "t.phase,t.request_witness_sequence,t.created_at "
        + "FROM claimcore.case_erasure_tombstones t "
        + "JOIN claimcore.case_erasure_authority_tip a ON a.case_id=t.case_id "
        + "WHERE t.case_id=@case AND t.phase IN "
        + "('ERASURE_PENDING','PAYLOAD_ERASED_SUPPRESSION_RETAINED','ERASURE_FINAL') "
        + "AND t.purge_event_id IS NOT NULL AND t.live_purged_at IS NOT NULL "
        + "FOR UPDATE OF a"

    let lock (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) caseId =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            Sql.uuid command "case" caseId
            use! reader = command.ExecuteReaderAsync()
            let! found = reader.ReadAsync()

            if not found then
                return None
            else
                let value =
                    {
                        CaseId = reader.GetGuid(0)
                        Phase = reader.GetString(10)
                        RequestWitnessSequence = reader.GetInt64(11)
                        RequestedAt = reader.GetFieldValue<DateTimeOffset>(12)
                        PurgeEventId = reader.GetGuid(1)
                        PurgeCanonical = reader.GetFieldValue<byte array>(2)
                        PurgeCandidateHash = reader.GetFieldValue<byte array>(3)
                        PurgeWitnessSequence = reader.GetInt64(4)
                        PurgeWitnessEpoch = reader.GetInt64(5)
                        PurgeWitnessHash = reader.GetFieldValue<byte array>(6)
                        AuthorityRevision = reader.GetInt64(7)
                        AuthorityHash = reader.GetFieldValue<byte array>(8)
                        PruneEventId = if reader.IsDBNull(9) then None else Some(reader.GetGuid(9))
                    }

                if reader.Read() then
                    raise (InvalidDataException("Erasure tombstone is duplicated."))

                return Some value
        }

    let activeHolds (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) caseId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT h.hold_id,h.review_on FROM claimcore.case_erasure_holds h "
                    + "LEFT JOIN claimcore.case_erasure_hold_releases r ON r.hold_id=h.hold_id "
                    + "WHERE h.case_id=@case AND r.hold_id IS NULL "
                    + "ORDER BY h.hold_id LIMIT 257",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            use! reader = command.ExecuteReaderAsync()
            let values = ResizeArray<Guid * DateOnly>()

            while reader.Read() do
                values.Add(reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1))

            if values.Count > 256 then
                raise (InvalidDataException("Erasure hold capacity was exceeded."))

            return values |> Seq.toList
        }
