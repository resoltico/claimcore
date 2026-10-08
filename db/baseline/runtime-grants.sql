
REVOKE ALL ON ALL TABLES IN SCHEMA claimcore FROM PUBLIC, claimcore_app;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA claimcore FROM PUBLIC, claimcore_app;
GRANT USAGE ON SCHEMA claimcore TO claimcore_app;
GRANT SELECT ON claimcore.schema_baseline, claimcore.installation_lineage,
    claimcore.writer_handoffs, claimcore.writer_handoff_preparations,
    claimcore.writer_activations,
    claimcore.writer_handoff_approval_uses,
    claimcore.writer_handoff_abort_approvals,
    claimcore.writer_handoff_aborts,
    claimcore.writer_handoff_abort_approval_uses TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.writer_handoff_approvals TO claimcore_app;
GRANT SELECT, UPDATE ON claimcore.authority_tip TO claimcore_app;
GRANT SELECT, INSERT, UPDATE ON claimcore.actors, claimcore.actor_grants TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.actor_authority_events TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.installation_data_use_approvals TO claimcore_app;
GRANT SELECT ON claimcore.installation_data_use_plans,
    claimcore.installation_data_use_activations TO claimcore_app;
GRANT SELECT ON claimcore.installation_data_use_approval_uses TO claimcore_app;
GRANT SELECT, INSERT, UPDATE ON claimcore.cases TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_lifecycle_events,
    claimcore.case_lifecycle_approvals TO claimcore_app;
GRANT SELECT, INSERT, UPDATE ON claimcore.case_holds TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_erasure_tombstones,
    claimcore.case_erasure_operation_denials TO claimcore_app;
GRANT SELECT, UPDATE ON claimcore.case_erasure_authority_tip TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_erasure_holds,
    claimcore.case_erasure_hold_releases TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_erasure_prune_approvals TO claimcore_app;
GRANT SELECT ON claimcore.case_erasure_prune_targets TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_erasure_terminal_approvals TO claimcore_app;
GRANT SELECT ON claimcore.case_erasure_terminal_events,
    claimcore.case_erasure_terminal_approval_uses TO claimcore_app;
GRANT SELECT ON claimcore.case_erasure_purge_approvals TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.recovery_artifact_exports TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.recovery_artifact_payloads TO claimcore_app;
GRANT SELECT ON claimcore.managed_copy_signers, claimcore.managed_copy_signer_events,
    claimcore.managed_copy_signer_approval_uses TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.managed_copy_signer_approvals TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.managed_copies TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.managed_copy_events TO claimcore_app;
GRANT SELECT ON claimcore.managed_copy_adoptions TO claimcore_app;
GRANT SELECT ON claimcore.managed_copy_external_publications TO claimcore_app;
GRANT SELECT ON claimcore.managed_copy_verifications TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.managed_copy_adoption_approvals TO claimcore_app;
GRANT SELECT ON claimcore.managed_copy_adoption_approval_uses TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.managed_copy_deletion_approvals TO claimcore_app;
GRANT SELECT ON claimcore.managed_copy_deletion_approval_uses TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_changes, claimcore.request_preparations,
    claimcore.request_preparation_lifecycle, claimcore.request_submission_attempts,
    claimcore.request_submission_settlements, claimcore.operation_revocations TO claimcore_app;
