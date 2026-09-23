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

    let effectiveTableAcl =
        [
            readWriteAcl "cases"
            readAppendAcl "case_changes"
            readOnlyAcl "schema_baseline"
            readOnlyAcl "installation_lineage"
            readAppendAcl "request_preparations"
            readAppendAcl "request_preparation_lifecycle"
            readAppendAcl "request_submission_attempts"
            readAppendAcl "request_submission_settlements"
            readAppendAcl "operation_revocations"
        ]
        |> String.concat ",\n        "

    let databaseAndSchemaCatalogAcl =
        """
        EXISTS (SELECT 1 FROM pg_database d CROSS JOIN LATERAL aclexplode(COALESCE(d.datacl, acldefault('d', d.datdba))) p CROSS JOIN runtime_role r WHERE d.datname = current_database() AND p.grantee IN (0, r.oid) AND (p.grantee = 0 OR p.is_grantable OR p.privilege_type <> 'CONNECT'))
        OR EXISTS (SELECT 1 FROM pg_namespace n CROSS JOIN LATERAL aclexplode(COALESCE(n.nspacl, acldefault('n', n.nspowner))) p CROSS JOIN runtime_role r WHERE n.nspname = 'claimcore' AND p.grantee IN (0, r.oid) AND (p.grantee = 0 OR p.is_grantable OR p.privilege_type <> 'USAGE'))
        """

    let tableCatalogAcl =
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
                  WHEN 'request_preparations' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'request_preparation_lifecycle' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'request_submission_attempts' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'request_submission_settlements' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  WHEN 'operation_revocations' THEN p.privilege_type NOT IN ('SELECT', 'INSERT')
                  ELSE TRUE END)
        )
        """

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
