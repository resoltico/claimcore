namespace ClaimCore.Postgres

exception internal RuntimeDatabaseMismatch

module internal RuntimeAccessPolicy =
    let roleSql =
        "SELECT current_user::text, session_user::text, "
        + "(NOT r.rolcanlogin OR r.rolsuper OR r.rolcreaterole OR r.rolcreatedb OR r.rolbypassrls OR r.rolreplication "
        + "OR EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member = r.oid) "
        + "OR EXISTS (SELECT 1 FROM pg_database d WHERE d.datname = current_database() AND d.datdba = r.oid) "
        + "OR EXISTS (SELECT 1 FROM pg_namespace n WHERE n.nspname = 'claimcore' AND n.nspowner = r.oid) "
        + "OR EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
        + "           WHERE n.nspname = 'claimcore' AND c.relowner = r.oid) "
        + "OR EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace "
        + "           WHERE n.nspname = 'claimcore' AND p.proowner = r.oid)) "
        + "FROM pg_roles r WHERE r.rolname = session_user"

    let effectiveDatabaseAndSchemaAcl =
        """
        has_database_privilege(current_user, current_database(), 'CONNECT'),
        has_database_privilege(current_user, current_database(), 'CREATE')
            OR has_database_privilege(current_user, current_database(), 'TEMPORARY')
            OR has_database_privilege(current_user, current_database(), 'CONNECT WITH GRANT OPTION'),
        has_schema_privilege(current_user, 'claimcore', 'USAGE'),
        has_schema_privilege(current_user, 'claimcore', 'CREATE')
            OR has_schema_privilege(current_user, 'claimcore', 'USAGE WITH GRANT OPTION')
        """

    let private tablePrivileges separator table privileges =
        privileges
        |> List.map (fun privilege ->
            $"has_table_privilege(current_user, 'claimcore.{table}', '{privilege}')")
        |> String.concat separator

    let private tableAcl table required forbidden =
        tablePrivileges "\n            AND " table required
        + ",\n        "
        + tablePrivileges "\n            OR " table forbidden

    let private readWriteAcl table =
        tableAcl
            table
            [ "SELECT"; "INSERT"; "UPDATE" ]
            [
                "DELETE"
                "TRUNCATE"
                "REFERENCES"
                "TRIGGER"
                "MAINTAIN"
                "SELECT WITH GRANT OPTION"
                "INSERT WITH GRANT OPTION"
                "UPDATE WITH GRANT OPTION"
            ]

    let private readAppendAcl table =
        tableAcl
            table
            [ "SELECT"; "INSERT" ]
            [
                "UPDATE"
                "DELETE"
                "TRUNCATE"
                "REFERENCES"
                "TRIGGER"
                "MAINTAIN"
                "SELECT WITH GRANT OPTION"
                "INSERT WITH GRANT OPTION"
            ]

    let private readOnlyAcl table =
        tableAcl
            table
            [ "SELECT" ]
            [
                "INSERT"
                "UPDATE"
                "DELETE"
                "TRUNCATE"
                "REFERENCES"
                "TRIGGER"
                "MAINTAIN"
                "SELECT WITH GRANT OPTION"
            ]

    let private authorityTipAcl name =
        tableAcl
            name
            [ "SELECT"; "UPDATE" ]
            [
                "INSERT"
                "DELETE"
                "TRUNCATE"
                "REFERENCES"
                "TRIGGER"
                "MAINTAIN"
                "SELECT WITH GRANT OPTION"
                "UPDATE WITH GRANT OPTION"
            ]

    let private caseAndAuthorityAcls =
        [
            readWriteAcl "cases"
            readAppendAcl "case_changes"
            readOnlyAcl "schema_baseline"
            readOnlyAcl "installation_lineage"
            readOnlyAcl "writer_handoffs"
            readOnlyAcl "writer_activations"
            readOnlyAcl "writer_handoff_preparations"
            readAppendAcl "writer_handoff_approvals"
            readOnlyAcl "writer_handoff_approval_uses"
            readOnlyAcl "writer_handoff_abort_approvals"
            readOnlyAcl "writer_handoff_aborts"
            readOnlyAcl "writer_handoff_abort_approval_uses"
            authorityTipAcl "authority_tip"
            readWriteAcl "actors"
            readWriteAcl "actor_grants"
            readAppendAcl "actor_authority_events"
            readAppendAcl "installation_data_use_approvals"
            readOnlyAcl "installation_data_use_plans"
            readOnlyAcl "installation_data_use_activations"
            readOnlyAcl "installation_data_use_approval_uses"
            readAppendAcl "recovery_artifact_exports"
            readAppendAcl "recovery_artifact_payloads"
        ]

    let private custodyAcls =
        [
            readOnlyAcl "managed_copy_signers"
            readOnlyAcl "managed_copy_signer_events"
            readAppendAcl "managed_copy_signer_approvals"
            readOnlyAcl "managed_copy_signer_approval_uses"
            readAppendAcl "managed_copies"
            readAppendAcl "managed_copy_events"
            readOnlyAcl "managed_copy_adoptions"
            readOnlyAcl "managed_copy_external_publications"
            readOnlyAcl "managed_copy_verifications"
            readAppendAcl "managed_copy_adoption_approvals"
            readOnlyAcl "managed_copy_adoption_approval_uses"
            readAppendAcl "managed_copy_deletion_approvals"
            readOnlyAcl "managed_copy_deletion_approval_uses"
        ]

    let private lifecycleAcls =
        [
            readAppendAcl "case_lifecycle_events"
            readAppendAcl "case_lifecycle_approvals"
            readWriteAcl "case_holds"
            readAppendAcl "case_erasure_tombstones"
            readAppendAcl "case_erasure_operation_denials"
            authorityTipAcl "case_erasure_authority_tip"
            readAppendAcl "case_erasure_holds"
            readAppendAcl "case_erasure_hold_releases"
            readAppendAcl "case_erasure_prune_approvals"
            readOnlyAcl "case_erasure_prune_targets"
            readAppendAcl "case_erasure_terminal_approvals"
            readOnlyAcl "case_erasure_terminal_events"
            readOnlyAcl "case_erasure_terminal_approval_uses"
            readOnlyAcl "case_erasure_purge_approvals"
        ]

    let private preparationAcls =
        [
            readAppendAcl "request_preparations"
            readAppendAcl "request_preparation_lifecycle"
            readAppendAcl "request_submission_attempts"
            readAppendAcl "request_submission_settlements"
            readAppendAcl "operation_revocations"
        ]

    let effectiveTableAcl =
        [ caseAndAuthorityAcls; custodyAcls; lifecycleAcls; preparationAcls ]
        |> List.concat
        |> String.concat ",\n        "

    let databaseAndSchemaCatalogAcl =
        """
        EXISTS (SELECT 1 FROM pg_database d CROSS JOIN LATERAL aclexplode(COALESCE(d.datacl, acldefault('d', d.datdba))) p CROSS JOIN runtime_role r WHERE d.datname = current_database() AND p.grantee IN (0, r.oid) AND (p.grantee = 0 OR p.is_grantable OR p.privilege_type <> 'CONNECT'))
        OR EXISTS (SELECT 1 FROM pg_namespace n CROSS JOIN LATERAL aclexplode(COALESCE(n.nspacl, acldefault('n', n.nspowner))) p CROSS JOIN runtime_role r WHERE n.nspname = 'claimcore' AND p.grantee IN (0, r.oid) AND (p.grantee = 0 OR p.is_grantable OR p.privilege_type <> 'USAGE'))
        """

    let private tableAclFoundation =
        """
        EXISTS (
            SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN LATERAL aclexplode(COALESCE(c.relacl, acldefault('r', c.relowner))) p
            CROSS JOIN runtime_role r
            WHERE n.nspname = 'claimcore' AND p.grantee IN (0, r.oid)
              AND (p.grantee = 0 OR p.is_grantable OR CASE c.relname
                  WHEN 'cases' THEN p.privilege_type NOT IN ('SELECT', 'INSERT', 'UPDATE')
                  WHEN 'case_changes' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'schema_baseline' THEN p.privilege_type <> 'SELECT'
                  WHEN 'installation_lineage' THEN p.privilege_type <> 'SELECT'
                  WHEN 'writer_handoffs' THEN p.privilege_type <> 'SELECT'
                  WHEN 'writer_activations' THEN p.privilege_type <> 'SELECT'
                  WHEN 'writer_handoff_preparations' THEN p.privilege_type <> 'SELECT'
                  WHEN 'writer_handoff_approvals' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'writer_handoff_approval_uses' THEN p.privilege_type <> 'SELECT'
                  WHEN 'writer_handoff_abort_approvals' THEN p.privilege_type <> 'SELECT'
                  WHEN 'writer_handoff_aborts' THEN p.privilege_type <> 'SELECT'
                  WHEN 'writer_handoff_abort_approval_uses' THEN p.privilege_type <> 'SELECT'
                  WHEN 'authority_tip' THEN p.privilege_type NOT IN ('SELECT', 'UPDATE')
                  WHEN 'actors' THEN p.privilege_type NOT IN ('SELECT', 'INSERT', 'UPDATE')
                  WHEN 'actor_grants' THEN p.privilege_type NOT IN ('SELECT', 'INSERT', 'UPDATE')
                  WHEN 'actor_authority_events' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'installation_data_use_approvals' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'installation_data_use_plans' THEN p.privilege_type <> 'SELECT'
                  WHEN 'installation_data_use_activations' THEN p.privilege_type <> 'SELECT'
                  WHEN 'installation_data_use_approval_uses' THEN p.privilege_type <> 'SELECT'
                  WHEN 'recovery_artifact_exports' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'recovery_artifact_payloads' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
"""

    let private tableAclManagedCopies =
        """                  WHEN 'managed_copy_signers' THEN p.privilege_type <> 'SELECT'
                  WHEN 'managed_copy_signer_events' THEN p.privilege_type <> 'SELECT'
                  WHEN 'managed_copy_signer_approvals' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'managed_copy_signer_approval_uses' THEN p.privilege_type <> 'SELECT'
                  WHEN 'managed_copies' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'managed_copy_events' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'managed_copy_adoptions' THEN p.privilege_type <> 'SELECT'
                  WHEN 'managed_copy_external_publications' THEN p.privilege_type <> 'SELECT'
                  WHEN 'managed_copy_verifications' THEN p.privilege_type <> 'SELECT'
                  WHEN 'managed_copy_adoption_approvals' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'managed_copy_adoption_approval_uses' THEN p.privilege_type <> 'SELECT'
                  WHEN 'managed_copy_deletion_approvals' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'managed_copy_deletion_approval_uses' THEN p.privilege_type <> 'SELECT'
"""

    let private tableAclErasure =
        """                  WHEN 'case_lifecycle_events' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'case_lifecycle_approvals' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'case_holds' THEN p.privilege_type NOT IN ('SELECT', 'INSERT', 'UPDATE')
                  WHEN 'case_erasure_tombstones' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'case_erasure_operation_denials' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'case_erasure_authority_tip' THEN p.privilege_type NOT IN ('SELECT', 'UPDATE')
                  WHEN 'case_erasure_holds' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'case_erasure_hold_releases' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'case_erasure_prune_approvals' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'case_erasure_prune_targets' THEN p.privilege_type <> 'SELECT'
                  WHEN 'case_erasure_terminal_approvals' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'case_erasure_terminal_events' THEN p.privilege_type <> 'SELECT'
                  WHEN 'case_erasure_terminal_approval_uses' THEN p.privilege_type <> 'SELECT'
                  WHEN 'case_erasure_purge_approvals' THEN p.privilege_type <> 'SELECT'
                  WHEN 'request_preparations' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'request_preparation_lifecycle' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'request_submission_attempts' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'request_submission_settlements' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'operation_revocations' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
"""

    let private tableAclClosure =
        """                  ELSE TRUE END)
        )
        """

    let tableCatalogAcl =
        tableAclFoundation + tableAclManagedCopies + tableAclErasure + tableAclClosure

    let columnCatalogAcl =
        """
        EXISTS (
            SELECT 1 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace CROSS JOIN LATERAL aclexplode(a.attacl) p
            CROSS JOIN runtime_role r
            WHERE n.nspname = 'claimcore' AND a.attnum > 0 AND NOT a.attisdropped
              AND p.grantee IN (0, r.oid)
        )
        """

    let aclSql =
        "WITH runtime_role AS (SELECT oid FROM pg_roles WHERE rolname = session_user) SELECT "
        + effectiveDatabaseAndSchemaAcl
        + ","
        + effectiveTableAcl
        + ", ("
        + databaseAndSchemaCatalogAcl
        + ") OR ("
        + tableCatalogAcl
        + "),"
        + columnCatalogAcl
        + " FROM runtime_role"
