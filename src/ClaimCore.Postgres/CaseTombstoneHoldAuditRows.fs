namespace ClaimCore.Postgres

open DataAuditCommon

open System
open Npgsql

[<NoEquality; NoComparison>]
type internal TombstoneHoldAuditRow =
    {
        Revision: int64
        EventId: Guid
        HoldId: Guid
        Kind: string
        Code: string
        ReviewOn: DateOnly option
        ActorId: Guid
        GrantRevision: int64
        ObservedAt: DateTimeOffset
        PreviousHash: byte array
        EventHash: byte array
        Canonical: byte array
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
    }

module internal CaseTombstoneHoldAuditRows =
    let private sql =
        "SELECT * FROM (SELECT record_revision AS revision,record_event_id AS event_id,"
        + "hold_id,'RECORD' AS kind,ground_code AS code,review_on,recorded_by AS actor_id,"
        + "recorded_grant_revision AS grant_revision,recorded_at AS observed_at,"
        + "record_previous_hash AS previous_hash,record_event_hash AS event_hash,"
        + "record_canonical_action AS canonical,record_candidate_sha256 AS candidate_hash,"
        + "record_witness_sequence AS witness_sequence,record_witness_epoch AS witness_epoch,"
        + "record_witness_hash AS witness_hash FROM claimcore.case_erasure_holds "
        + "WHERE case_id=@case AND record_revision>@after "
        + "UNION ALL SELECT release_revision,release_event_id,hold_id,'RELEASE',"
        + "release_code,NULL::date,released_by,released_grant_revision,released_at,"
        + "release_previous_hash,release_event_hash,release_canonical_action,"
        + "release_candidate_sha256,release_witness_sequence,release_witness_epoch,"
        + "release_witness_hash FROM claimcore.case_erasure_hold_releases "
        + "WHERE case_id=@case AND release_revision>@after "
        + "UNION ALL SELECT authority_revision,terminal_event_id,"
        + "'00000000-0000-0000-0000-000000000000'::uuid,'TERMINAL',action_name,"
        + "NULL::date,'00000000-0000-0000-0000-000000000000'::uuid,0,recorded_at,"
        + "previous_authority_hash,authority_hash,canonical_action,candidate_sha256,"
        + "witness_sequence,witness_epoch,witness_entry_hash "
        + "FROM claimcore.case_erasure_terminal_events "
        + "WHERE case_id=@case AND authority_revision>@after) events "
        + "ORDER BY revision LIMIT 50"

    let page (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) caseId after =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            Sql.uuid command "case" caseId
            Sql.integer command "after" after
            use! reader = command.ExecuteReaderAsync()
            let rows = ResizeArray<TombstoneHoldAuditRow>()

            while reader.Read() do
                rows.Add
                    {
                        Revision = reader.GetInt64(0)
                        EventId = reader.GetGuid(1)
                        HoldId = reader.GetGuid(2)
                        Kind = reader.GetString(3)
                        Code = reader.GetString(4)
                        ReviewOn =
                            if reader.IsDBNull(5) then
                                None
                            else
                                Some(reader.GetFieldValue<DateOnly>(5))
                        ActorId = reader.GetGuid(6)
                        GrantRevision = reader.GetInt64(7)
                        ObservedAt = reader.GetFieldValue<DateTimeOffset>(8)
                        PreviousHash = reader.GetFieldValue<byte array>(9)
                        EventHash = reader.GetFieldValue<byte array>(10)
                        Canonical = reader.GetFieldValue<byte array>(11)
                        CandidateHash = reader.GetFieldValue<byte array>(12)
                        WitnessSequence = reader.GetInt64(13)
                        WitnessEpoch = reader.GetInt64(14)
                        WitnessHash = reader.GetFieldValue<byte array>(15)
                    }

            return rows |> Seq.toList
        }

    let readTip (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) caseId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT revision,event_hash FROM claimcore.case_erasure_authority_tip WHERE case_id=@case",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                corrupt ()

            let value = reader.GetInt64(0), reader.GetFieldValue<byte array>(1)

            if reader.Read() then
                corrupt ()

            return value
        }
