namespace ClaimCore.Postgres

/// Critical business and accepted-history constraints must remain live, not merely named.
module internal RuntimeConstraintPolicy =
    let sql =
        """
        , (
            SELECT count(c.oid) = 20
                AND bool_and(c.contype::text = required.kind
                    AND c.convalidated AND c.conenforced
                    AND NOT c.condeferrable AND NOT c.condeferred
                    AND (required.kind <> 'f' OR (
                        c.confrelid = 'claimcore.cases'::regclass
                        AND c.confupdtype = 'a' AND c.confdeltype = 'a')))
            FROM (VALUES
                ('cases', 'cases_pkey', 'p'),
                ('cases', 'cases_status_check', 'c'),
                ('cases', 'cases_revision_check', 'c'),
                ('cases', 'reference_shape', 'c'),
                ('cases', 'name_shape', 'c'),
                ('cases', 'claimed_money', 'c'),
                ('cases', 'decision_group', 'c'),
                ('cases', 'payable_money', 'c'),
                ('cases', 'date_bounds', 'c'),
                ('cases', 'paid_requires_decision', 'c'),
                ('case_changes', 'case_changes_pkey', 'p'),
                ('case_changes', 'case_changes_case_reference_revision_key', 'u'),
                ('case_changes', 'case_changes_case_reference_fkey', 'f'),
                ('case_changes', 'case_changes_operation_id_check', 'c'),
                ('case_changes', 'case_changes_revision_check', 'c'),
                ('case_changes', 'case_changes_command_name_check', 'c'),
                ('case_changes', 'case_changes_request_format_version_check', 'c'),
                ('case_changes', 'case_changes_request_sha256_check', 'c'),
                ('case_changes', 'case_changes_snapshot_version_check', 'c'),
                ('case_changes', 'case_changes_snapshot_check', 'c')
            ) AS required(relation_name, constraint_name, kind)
            JOIN pg_catalog.pg_namespace n ON n.nspname = 'claimcore'
            LEFT JOIN pg_catalog.pg_class t ON t.relnamespace = n.oid
                AND t.relname = required.relation_name
            LEFT JOIN pg_catalog.pg_constraint c ON c.conrelid = t.oid
                AND c.conname::text = required.constraint_name
        )
        """
