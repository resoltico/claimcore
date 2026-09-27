namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open DataAuditCommon

/// The per-case authority replay verifies the exact event/approval/witness chain. This global
/// bounded pass closes the orphan gap: every terminal event must also be the tombstone's
/// projected copy/final event, including cases otherwise outside the purged-case audit query.
module internal DataAuditTerminalEvents =
    let private page connection transaction after (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT e.terminal_event_id,e.case_id,CASE WHEN e.resulting_phase<>'ERASURE_FINAL' "
                    + "THEN e.recovery_fence_digest IS NULL ELSE EXISTS ("
                    + "SELECT 1 FROM claimcore.writer_activations a "
                    + "JOIN claimcore.writer_handoffs h ON h.handoff_id=a.handoff_id "
                    + "WHERE a.writer_generation=e.writer_generation "
                    + "AND a.witness_epoch=e.witness_epoch "
                    + "AND a.probe_evidence_sha256=e.recovery_fence_digest "
                    + "AND a.witness_sequence<e.witness_sequence "
                    + "AND h.old_generation+1=h.new_generation "
                    + "AND h.new_generation=e.writer_generation "
                    + "AND COALESCE((SELECT MAX(x.witness_sequence) "
                    + "FROM claimcore.recovery_artifact_exports x WHERE x.case_id=e.case_id),1) "
                    + "<a.witness_sequence "
                    + "AND NOT EXISTS(SELECT 1 FROM claimcore.recovery_artifact_exports x "
                    + "WHERE x.case_id=e.case_id AND x.expires_at>e.recorded_at) "
                    + "AND NOT EXISTS(SELECT 1 FROM claimcore.recovery_artifact_payloads p "
                    + "JOIN claimcore.recovery_artifact_exports x ON x.export_id=p.export_id "
                    + "WHERE x.case_id=e.case_id)) END "
                    + "FROM claimcore.case_erasure_terminal_events e "
                    + "WHERE e.terminal_event_id>@after ORDER BY e.terminal_event_id LIMIT 50",
                    connection,
                    transaction
                )

            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let values = ResizeArray<Guid * Guid * bool>()

            while reader.Read() do
                values.Add(reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2))

            return values |> Seq.toList
        }

    let private projected connection transaction eventId caseId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.case_erasure_tombstones "
                    + "WHERE case_id=@case AND phase IN "
                    + "('PAYLOAD_ERASED_SUPPRESSION_RETAINED','ERASURE_FINAL') "
                    + "AND (copy_absence_event_id=@event OR suppression_final_event_id=@event))",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            Sql.uuid command "event" eventId
            let! result = command.ExecuteScalarAsync(ct)

            match result with
            | :? bool as exists when exists -> return ()
            | _ -> corrupt ()
        }

    let verify connection transaction (ct: CancellationToken) =
        task {
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                let! rows = page connection transaction after ct

                for eventId, caseId, recoveryFenceValid in rows do
                    if not recoveryFenceValid then
                        corrupt ()

                    do! projected connection transaction eventId caseId ct
                    after <- eventId
                    count <- count + 1L

                more <- rows.Length = 50

            return count
        }
