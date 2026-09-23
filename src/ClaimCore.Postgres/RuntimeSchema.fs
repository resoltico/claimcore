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
        """

    let private preparationColumnChecks =
        """
            (SELECT count(*) = 1 AND bool_and(singleton AND lineage_id <> '00000000-0000-0000-0000-000000000000') FROM claimcore.installation_lineage),
            (SELECT array_agg(a.attname::text ORDER BY a.attnum) = ARRAY[
                'singleton',
                'lineage_id',
                'created_at',
                'business_time_zone'
            ] FROM pg_attribute a
                JOIN pg_class c ON c.oid = a.attrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'claimcore'
                AND c.relname = 'installation_lineage'
                AND a.attnum > 0
                AND NOT a.attisdropped),
            (SELECT array_agg(a.attname::text ORDER BY a.attnum) = ARRAY[
                'operation_id',
                'canonical_request_format',
                'request_sha256',
                'canonical_request',
                'prepared_at',
                'prepared_application_version',
                'preparing_contract_fingerprint',
                'preparing_contract_kind'
            ] FROM pg_attribute a
                JOIN pg_class c ON c.oid = a.attrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'claimcore'
                AND c.relname = 'request_preparations'
                AND a.attnum > 0
                AND NOT a.attisdropped),
            (SELECT count(*) = 3 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'claimcore' AND c.relname = 'request_preparation_lifecycle' AND a.attnum > 0 AND NOT a.attisdropped),
            (SELECT count(*) = 9 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'claimcore' AND c.relname = 'request_preparation_prunes' AND a.attnum > 0 AND NOT a.attisdropped),
            (SELECT count(*) = 3 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'claimcore' AND c.relname = 'request_submission_attempts' AND a.attnum > 0 AND NOT a.attisdropped),
            (SELECT count(*) = 3 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'claimcore' AND c.relname = 'request_submission_settlements' AND a.attnum > 0 AND NOT a.attisdropped),
        """

    let private authorityChecks =
        """
            (SELECT array_agg(a.attname::text ORDER BY a.attnum) = ARRAY[
                'operation_id',
                'canonical_request_format',
                'request_sha256',
                'revoked_at',
                'reason'
            ] FROM pg_attribute a
                JOIN pg_class c ON c.oid = a.attrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'claimcore'
                AND c.relname = 'operation_revocations'
                AND a.attnum > 0
                AND NOT a.attisdropped),
            EXISTS (
                SELECT 1
                FROM pg_constraint constraint_value
                JOIN pg_class relation ON relation.oid = constraint_value.conrelid
                JOIN pg_namespace schema_value ON schema_value.oid = relation.relnamespace
                WHERE schema_value.nspname = 'claimcore'
                    AND relation.relname = 'request_submission_settlements'
                    AND constraint_value.conname = 'request_submission_settlements_outcome_check'
                    AND constraint_value.contype = 'c'
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
                    AND position('CORRECT_CASE' IN pg_get_constraintdef(constraint_value.oid)) > 0
            ),
            EXISTS (
                SELECT 1
                FROM pg_constraint constraint_value
                JOIN pg_class relation ON relation.oid = constraint_value.conrelid
                JOIN pg_namespace schema_value ON schema_value.oid = relation.relnamespace
                WHERE schema_value.nspname = 'claimcore'
                    AND relation.relname = 'installation_lineage'
                    AND constraint_value.conname = 'installation_lineage_business_time_zone_shape'
                    AND constraint_value.contype = 'c'
            )
        """

    let private freshChecks =
        """,
            (SELECT bool_and(a.attnotnull) FROM pg_catalog.pg_attribute a
                WHERE a.attrelid = 'claimcore.installation_lineage'::regclass
                    AND a.attnum > 0 AND NOT a.attisdropped),
            (SELECT count(*) = 2 AND bool_and(position('(canonical_request_format = 3)' IN pg_get_constraintdef(c.oid)) > 0)
                FROM pg_catalog.pg_constraint c WHERE c.conrelid IN (
                    'claimcore.request_preparations'::regclass, 'claimcore.operation_revocations'::regclass
                ) AND c.conname IN ('request_preparations_canonical_request_format_check', 'operation_revocations_canonical_request_format_check')
                    AND c.contype = 'c' AND c.convalidated),
            EXISTS (SELECT 1 FROM pg_catalog.pg_constraint c
                WHERE c.conrelid = 'claimcore.request_preparations'::regclass
                    AND c.conname = 'request_preparations_preparing_contract_kind_check'
                    AND c.contype = 'c' AND c.convalidated
                    AND position('CANONICAL_RECORD_V3' IN pg_get_constraintdef(c.oid)) > 0
                    AND position('LEGACY' IN pg_get_constraintdef(c.oid)) = 0),
            EXISTS (SELECT 1 FROM pg_catalog.pg_constraint c
                WHERE c.conrelid = 'claimcore.operation_revocations'::regclass
                    AND c.conname = 'operation_revocations_reason_check'
                    AND c.contype = 'c' AND c.convalidated
                    AND position('OPERATOR_DISMISSAL' IN pg_get_constraintdef(c.oid)) > 0
                    AND position('LEGACY' IN pg_get_constraintdef(c.oid)) = 0),
            EXISTS (SELECT 1 FROM pg_catalog.pg_constraint c
                WHERE c.conrelid = 'claimcore.request_preparation_lifecycle'::regclass
                    AND c.conname = 'request_preparation_lifecycle_state_check'
                    AND c.contype = 'c' AND c.convalidated
                    AND position('SUBMISSION_STARTED' IN pg_get_constraintdef(c.oid)) > 0
                    AND position('DISMISSED' IN pg_get_constraintdef(c.oid)) = 0)
        """

    let private preparationSql =
        preparationRelationChecks
        + preparationColumnChecks
        + authorityChecks
        + freshChecks

    let private requirePreparation (reader: DbDataReader) =
        for index in 0 .. reader.FieldCount - 1 do
            if reader.IsDBNull(index) || not (reader.GetBoolean(index)) then
                raise (
                    InvalidDataException($"Required recovery schema component {index} is absent.")
                )

    let requireCompatible (connection: NpgsqlConnection) =
        if SchemaAdmission.inspect connection <> SchemaAdmissionState.Current then
            raise RuntimeDatabaseMismatch

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
