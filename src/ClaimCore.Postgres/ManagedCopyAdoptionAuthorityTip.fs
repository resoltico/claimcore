namespace ClaimCore.Postgres

open System
open Npgsql
open DataAuditCommon

/// Adoption binds the exact already-audited tombstone authority revision/hash. Later holds
/// cannot rewrite that historical tip, and an active hold at adoption is independently denied.
module internal ManagedCopyAdoptionAuthorityTip =
    let private tipSql =
        "SELECT event_hash,observed_at FROM ("
        + "SELECT record_event_hash AS event_hash,recorded_at AS observed_at "
        + "FROM claimcore.case_erasure_holds WHERE case_id=@case AND record_revision=@revision "
        + "UNION ALL SELECT release_event_hash,released_at "
        + "FROM claimcore.case_erasure_hold_releases "
        + "WHERE case_id=@case AND release_revision=@revision "
        + "UNION ALL SELECT authority_hash,recorded_at "
        + "FROM claimcore.case_erasure_terminal_events "
        + "WHERE case_id=@case AND authority_revision=@revision) history LIMIT 2"

    let private historical connection transaction caseId revision hash adoptedAt =
        task {
            if revision = 0L then
                if hash <> Array.zeroCreate<byte> 32 then
                    corrupt ()
            else
                use command = new NpgsqlCommand(tipSql, connection, transaction)
                Sql.uuid command "case" caseId
                Sql.integer command "revision" revision
                use! reader = command.ExecuteReaderAsync()

                if
                    not (reader.Read())
                    || reader.GetFieldValue<byte array>(0) <> hash
                    || reader.GetFieldValue<DateTimeOffset>(1) > adoptedAt
                    || reader.Read()
                then
                    corrupt ()
        }

    let private holdsSql =
        "SELECT EXISTS(SELECT 1 FROM claimcore.case_erasure_holds h "
        + "LEFT JOIN claimcore.case_erasure_hold_releases r ON r.hold_id=h.hold_id "
        + "WHERE h.case_id=@case AND h.record_witness_sequence<@sequence "
        + "AND (r.hold_id IS NULL OR r.release_witness_sequence>@sequence))"

    let verify connection transaction caseId revision hash adoptedAt witnessSequence =
        task {
            do! historical connection transaction caseId revision hash adoptedAt
            use command = new NpgsqlCommand(holdsSql, connection, transaction)
            Sql.uuid command "case" caseId
            Sql.integer command "sequence" witnessSequence
            let! held = command.ExecuteScalarAsync()

            if unbox<bool> held then
                corrupt ()
        }
