namespace ClaimCore.Postgres

open System
open Npgsql

/// Read-only primary lookups used by the independent witness journal replay.
module internal DataAuditJournalQueries =
    let keyed (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) sql =
        let command = new NpgsqlCommand(sql, connection, transaction)
        Sql.uuid command "operation" Guid.Empty
        command

    let authority =
        "SELECT EXISTS (SELECT 1 FROM claimcore.installation_data_use_activations "
        + "WHERE activation_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.installation_data_use_plans "
        + "WHERE plan_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.installation_data_use_approvals "
        + "WHERE approval_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.actor_authority_events WHERE event_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.case_lifecycle_events WHERE event_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.case_lifecycle_approvals WHERE approval_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.recovery_artifact_exports WHERE export_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.managed_copy_signer_approvals WHERE approval_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.managed_copy_deletion_approvals WHERE approval_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.managed_copy_adoption_approvals WHERE approval_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.managed_copy_adoptions WHERE adoption_event_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.managed_copy_external_publications WHERE publication_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.managed_copy_signer_events WHERE event_id=@operation) "
        + "OR EXISTS (SELECT 1 FROM claimcore.managed_copy_events WHERE event_id=@operation)"
        + " OR EXISTS (SELECT 1 FROM claimcore.writer_handoffs WHERE handoff_id=@operation)"
        + " OR EXISTS (SELECT 1 FROM claimcore.writer_activations WHERE activation_id=@operation)"
        + " OR EXISTS (SELECT 1 FROM claimcore.writer_handoff_approvals WHERE approval_id=@operation)"
        + " OR EXISTS (SELECT 1 FROM claimcore.case_erasure_holds WHERE record_event_id=@operation)"
        + " OR EXISTS (SELECT 1 FROM claimcore.case_erasure_hold_releases "
        + "WHERE release_event_id=@operation)"
        + " OR EXISTS (SELECT 1 FROM claimcore.case_erasure_prune_approvals "
        + "WHERE approval_id=@operation)"
        + " OR EXISTS (SELECT 1 FROM claimcore.case_erasure_terminal_approvals "
        + "WHERE approval_id=@operation)"
        + " OR EXISTS (SELECT 1 FROM claimcore.case_erasure_terminal_events "
        + "WHERE terminal_event_id=@operation)"
        + " OR EXISTS (SELECT 1 FROM claimcore.case_erasure_tombstones "
        + "WHERE purge_event_id=@operation OR witness_prune_event_id=@operation)"

    let sealedCase =
        "SELECT EXISTS (SELECT 1 FROM claimcore.case_erasure_tombstones "
        + "WHERE case_id=@case AND phase IN "
        + "('ERASURE_PENDING','PAYLOAD_ERASED_SUPPRESSION_RETAINED','ERASURE_FINAL') "
        + "AND purge_event_id IS NOT NULL "
        + "AND purge_witness_cutoff_sequence>=@sequence AND purge_witness_sequence>@sequence)"

    let technical =
        "SELECT EXISTS (SELECT 1 FROM claimcore.request_preparations "
        + "WHERE witness_event_id=@operation) OR EXISTS (SELECT 1 FROM "
        + "claimcore.request_submission_attempts WHERE witness_event_id=@operation)"

    let terminal =
        "SELECT case_id,request_sha256 FROM claimcore.case_changes "
        + "WHERE operation_id=@operation UNION ALL SELECT case_id,request_sha256 "
        + "FROM claimcore.operation_revocations WHERE operation_id=@operation"
