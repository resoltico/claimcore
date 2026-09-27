namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql

module internal RuntimeSchema =
    let private preparationRelationChecks =
        """
        SELECT
            to_regclass('claimcore.installation_lineage') IS NOT NULL,
            to_regclass('claimcore.installation_data_use_activations') IS NOT NULL,
            to_regclass('claimcore.installation_data_use_plans') IS NOT NULL,
            to_regclass('claimcore.installation_data_use_approvals') IS NOT NULL,
            to_regclass('claimcore.installation_data_use_approval_uses') IS NOT NULL,
            to_regclass('claimcore.writer_handoffs') IS NOT NULL,
            to_regclass('claimcore.writer_activations') IS NOT NULL,
            to_regclass('claimcore.writer_handoff_preparations') IS NOT NULL,
            to_regclass('claimcore.writer_handoff_approvals') IS NOT NULL,
            to_regclass('claimcore.writer_handoff_approval_uses') IS NOT NULL,
            to_regclass('claimcore.writer_handoff_abort_approvals') IS NOT NULL,
            to_regclass('claimcore.writer_handoff_aborts') IS NOT NULL,
            to_regclass('claimcore.writer_handoff_abort_approval_uses') IS NOT NULL,
            to_regclass('claimcore.request_preparations') IS NOT NULL,
            to_regclass('claimcore.request_preparation_lifecycle') IS NOT NULL,
            to_regclass('claimcore.request_preparation_prunes') IS NOT NULL,
            to_regclass('claimcore.request_preparation_lifecycle_retention') IS NOT NULL,
            to_regclass('claimcore.request_submission_attempts') IS NOT NULL,
            to_regclass('claimcore.request_submission_settlements') IS NOT NULL,
            to_regclass('claimcore.request_submission_attempts_by_operation') IS NOT NULL,
            to_regclass('claimcore.operation_revocations') IS NOT NULL,
            to_regclass('claimcore.operation_revocations_by_revoked_at') IS NOT NULL,
            to_regclass('claimcore.request_preparations_by_prepared_at') IS NOT NULL,
            to_regclass('claimcore.managed_copy_signers') IS NOT NULL,
            to_regclass('claimcore.managed_copy_signer_approvals') IS NOT NULL,
            to_regclass('claimcore.managed_copy_signer_approvals_target') IS NOT NULL,
            to_regclass('claimcore.managed_copy_signer_events') IS NOT NULL,
            to_regclass('claimcore.managed_copy_signer_approval_uses') IS NOT NULL,
            to_regclass('claimcore.managed_copies') IS NOT NULL,
            to_regclass('claimcore.managed_copy_events') IS NOT NULL,
            to_regclass('claimcore.managed_copy_verifications') IS NOT NULL,
            to_regclass('claimcore.managed_copy_deletion_approvals') IS NOT NULL,
            to_regclass('claimcore.managed_copy_deletion_approvals_by_copy') IS NOT NULL,
            to_regclass('claimcore.managed_copy_deletion_approval_uses') IS NOT NULL,
            to_regclass('claimcore.managed_copy_events_by_copy') IS NOT NULL,
            to_regclass('claimcore.managed_copies_by_state') IS NOT NULL,
            to_regclass('claimcore.managed_copies_by_case') IS NOT NULL,
            to_regclass('claimcore.recovery_artifact_exports') IS NOT NULL,
            to_regclass('claimcore.recovery_artifact_exports_by_key') IS NOT NULL,
            to_regclass('claimcore.case_lifecycle_events') IS NOT NULL,
            to_regclass('claimcore.case_lifecycle_approvals') IS NOT NULL,
            to_regclass('claimcore.case_holds') IS NOT NULL,
            to_regclass('claimcore.case_holds_active_by_case') IS NOT NULL,
        """

    let private lineageDataCheck =
        """
            (SELECT count(*) = 1 AND bool_and(singleton
                AND lineage_id <> '00000000-0000-0000-0000-000000000000'
                AND data_use_scope IN ('SYNTHETIC_ONLY','REAL_DATA')
                AND data_use_phase IN ('BOOTSTRAP_NO_CASES','ACTIVE'))
                FROM claimcore.installation_lineage),
            (SELECT count(*) = 1 AND bool_and(singleton AND revision >= 0) FROM claimcore.authority_tip),
        """

    let private authorityChecks =
        """
            EXISTS (
                SELECT 1
                FROM pg_constraint constraint_value
                JOIN pg_class relation ON relation.oid = constraint_value.conrelid
                JOIN pg_namespace schema_value ON schema_value.oid = relation.relnamespace
                WHERE schema_value.nspname = 'claimcore'
                    AND relation.relname = 'request_submission_settlements'
                    AND constraint_value.conname = 'request_submission_settlements_outcome_check'
                    AND constraint_value.contype = 'c'
                    AND constraint_value.convalidated AND constraint_value.conenforced
                    AND position('REVOKED_BEFORE_EXECUTION' IN pg_get_constraintdef(constraint_value.oid)) > 0
            ),
            EXISTS (
                SELECT 1
                FROM pg_constraint constraint_value
                JOIN pg_class relation ON relation.oid = constraint_value.conrelid
                JOIN pg_namespace schema_value ON schema_value.oid = relation.relnamespace
                WHERE schema_value.nspname = 'claimcore'
                    AND relation.relname = 'case_changes'
                    AND constraint_value.conname = 'case_changes_command_name_check'
                    AND constraint_value.contype = 'c'
                    AND constraint_value.convalidated AND constraint_value.conenforced
                    AND position('CORRECT_CASE' IN pg_get_constraintdef(constraint_value.oid)) > 0
            ),
        """

    let private authorityCalendarChecks =
        """
            EXISTS (
                SELECT 1
                FROM pg_constraint constraint_value
                JOIN pg_class relation ON relation.oid = constraint_value.conrelid
                JOIN pg_namespace schema_value ON schema_value.oid = relation.relnamespace
                WHERE schema_value.nspname = 'claimcore'
                    AND relation.relname = 'installation_lineage'
                    AND constraint_value.conname = 'installation_lineage_business_time_zone_shape'
                    AND constraint_value.contype = 'c'
                    AND constraint_value.convalidated AND constraint_value.conenforced
            )
            ,EXISTS (
                SELECT 1 FROM pg_constraint c
                WHERE c.conrelid='claimcore.installation_lineage'::regclass
                  AND c.conname='installation_lineage_data_use_shape'
                  AND c.contype='c' AND c.convalidated AND c.conenforced
            )
        """

    let private freshChecks =
        """,
            (SELECT bool_and(a.attnotnull) FROM pg_catalog.pg_attribute a
                WHERE a.attrelid = 'claimcore.installation_lineage'::regclass
                    AND a.attnum > 0 AND NOT a.attisdropped
                    AND a.attname NOT IN ('writer_handoff_event_id',
                        'data_use_activation_event_id','data_use_activation_sequence',
                        'data_use_activation_hash',
                        'writer_handoff_sequence', 'writer_handoff_hash',
                        'writer_activation_event_id', 'writer_activation_sequence',
                        'writer_activation_hash',
                        'last_aborted_handoff_id', 'last_aborted_handoff_sequence',
                        'last_aborted_handoff_hash')),
            EXISTS (SELECT 1 FROM pg_catalog.pg_constraint c
                WHERE c.conrelid = 'claimcore.installation_lineage'::regclass
                    AND c.conname = 'installation_lineage_writer_handoff_shape'
                    AND c.contype = 'c' AND c.convalidated AND c.conenforced
                    AND position('writer_activation_pending' IN pg_get_constraintdef(c.oid)) > 0),
            EXISTS (SELECT 1 FROM pg_catalog.pg_constraint c
                WHERE c.conrelid = 'claimcore.installation_lineage'::regclass
                    AND c.conname = 'installation_lineage_abort_ticket_shape'
                    AND c.contype = 'c' AND c.convalidated AND c.conenforced),
            (SELECT count(*) = 2 AND bool_and(c.convalidated AND c.conenforced AND position('(canonical_request_format = 3)' IN pg_get_constraintdef(c.oid)) > 0)
                FROM pg_catalog.pg_constraint c WHERE c.conrelid IN (
                    'claimcore.request_preparations'::regclass, 'claimcore.operation_revocations'::regclass
                ) AND c.conname IN ('request_preparations_canonical_request_format_check', 'operation_revocations_canonical_request_format_check')
                    AND c.contype = 'c' AND c.convalidated),
            EXISTS (SELECT 1 FROM pg_catalog.pg_constraint c
                WHERE c.conrelid = 'claimcore.request_preparations'::regclass
                    AND c.conname = 'request_preparations_preparing_contract_kind_check'
                    AND c.contype = 'c' AND c.convalidated AND c.conenforced
                    AND position('SEMANTIC_CORE_V1' IN pg_get_constraintdef(c.oid)) > 0
                    AND position('CANONICAL_RECORD_V3' IN pg_get_constraintdef(c.oid)) = 0
                    AND position('LEGACY' IN pg_get_constraintdef(c.oid)) = 0),
            EXISTS (SELECT 1 FROM pg_catalog.pg_constraint c
                WHERE c.conrelid = 'claimcore.operation_revocations'::regclass
                    AND c.conname = 'operation_revocations_reason_check'
                    AND c.contype = 'c' AND c.convalidated AND c.conenforced
                    AND position('OPERATOR_DISMISSAL' IN pg_get_constraintdef(c.oid)) > 0
                    AND position('LEGACY' IN pg_get_constraintdef(c.oid)) = 0),
            EXISTS (SELECT 1 FROM pg_catalog.pg_constraint c
                WHERE c.conrelid = 'claimcore.request_preparation_lifecycle'::regclass
                    AND c.conname = 'request_preparation_lifecycle_state_check'
                    AND c.contype = 'c' AND c.convalidated AND c.conenforced
                    AND position('SUBMISSION_STARTED' IN pg_get_constraintdef(c.oid)) > 0
                    AND position('DISMISSED' IN pg_get_constraintdef(c.oid)) = 0)
        """

    let private preparationSql =
        preparationRelationChecks
        + lineageDataCheck
        + authorityChecks
        + authorityCalendarChecks
        + freshChecks
        + RuntimeConstraintPolicy.sql

    let private requirePreparation (reader: DbDataReader) =
        for index in 0 .. reader.FieldCount - 1 do
            if reader.IsDBNull(index) || not (reader.GetBoolean(index)) then
                raise (
                    InvalidDataException($"Required recovery schema component {index} is absent.")
                )

    let requireCompatible (connection: NpgsqlConnection) =
        if SchemaAdmission.inspect connection <> SchemaAdmissionState.Current then
            raise RuntimeDatabaseMismatch

        CatalogManifest.requireCompatible connection
        use command = new NpgsqlCommand(preparationSql, connection)
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            raise RuntimeDatabaseMismatch

        requirePreparation reader

    let requireCompatibleAsyncWithCancellation
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        =
        task {
            let! installed = SchemaAdmission.inspectAsync connection cancellationToken

            if installed <> SchemaAdmissionState.Current then
                return raise RuntimeDatabaseMismatch

            do! CatalogManifest.requireCompatibleAsyncWithCancellation connection cancellationToken

            use command = new NpgsqlCommand(preparationSql, connection)
            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let! hasRow = reader.ReadAsync(cancellationToken)

            if not hasRow then
                return raise RuntimeDatabaseMismatch

            requirePreparation reader
        }

    let requireCompatibleAsync (connection: NpgsqlConnection) =
        requireCompatibleAsyncWithCancellation connection CancellationToken.None
