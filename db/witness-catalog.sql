-- Deterministic PostgreSQL 18.6 witness catalog projection. No OIDs or timestamps.
WITH target AS (
    SELECT oid, nspowner, nspacl FROM pg_namespace WHERE nspname = 'claimcore_witness'
), objects AS (
    SELECT jsonb_build_object(
        'schemaOwner', nspowner::regrole::text,
        'schemaAcl', nspacl::text,
        'types', (
            SELECT jsonb_agg(jsonb_build_object(
                'name', t.typname, 'kind', t.typtype, 'owner', t.typowner::regrole::text,
                'acl', t.typacl::text, 'element', format_type(t.typelem, NULL),
                'relation', c.relname,
                'attributes', (
                    SELECT jsonb_agg(jsonb_build_object(
                        'number', a.attnum, 'name', a.attname,
                        'type', format_type(a.atttypid, a.atttypmod),
                        'notNull', a.attnotnull, 'collation', co.collname, 'acl', a.attacl::text
                    ) ORDER BY a.attnum)
                    FROM pg_attribute a LEFT JOIN pg_collation co ON co.oid=a.attcollation
                    WHERE a.attrelid=t.typrelid AND a.attnum>0 AND NOT a.attisdropped
                )
            ) ORDER BY t.typname)
            FROM pg_type t LEFT JOIN pg_class c ON c.oid=t.typrelid
            WHERE t.typnamespace=target.oid
        ),
        'relations', (
            SELECT jsonb_agg(jsonb_build_object(
                'name', c.relname, 'kind', c.relkind, 'persistence', c.relpersistence,
                'owner', c.relowner::regrole::text, 'acl', c.relacl::text,
                'rls', c.relrowsecurity, 'forceRls', c.relforcerowsecurity
            ) ORDER BY c.relname)
            FROM pg_class c WHERE c.relnamespace = target.oid
        ),
        'columns', (
            SELECT jsonb_agg(jsonb_build_object(
                'relation', c.relname, 'number', a.attnum, 'name', a.attname,
                'type', format_type(a.atttypid, a.atttypmod), 'notNull', a.attnotnull,
                'collation', co.collname, 'identity', a.attidentity,
                'generated', a.attgenerated, 'acl', a.attacl::text,
                'default', pg_get_expr(d.adbin, d.adrelid)
            ) ORDER BY c.relname, a.attnum)
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            LEFT JOIN pg_collation co ON co.oid = a.attcollation
            WHERE c.relnamespace = target.oid AND a.attnum > 0 AND NOT a.attisdropped
        ),
        'constraints', (
            SELECT jsonb_agg(jsonb_build_object(
                'relation', c.relname, 'name', con.conname, 'type', con.contype,
                'definition', pg_get_constraintdef(con.oid), 'validated', con.convalidated,
                'enforced', con.conenforced, 'deferrable', con.condeferrable,
                'deferred', con.condeferred
            ) ORDER BY c.relname, con.conname)
            FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid
            WHERE c.relnamespace = target.oid
        ),
        'indexes', (
            SELECT jsonb_agg(jsonb_build_object(
                'name', idx.relname, 'table', tbl.relname,
                'definition', pg_get_indexdef(i.indexrelid), 'valid', i.indisvalid,
                'ready', i.indisready, 'live', i.indislive
            ) ORDER BY idx.relname)
            FROM pg_index i JOIN pg_class idx ON idx.oid = i.indexrelid
            JOIN pg_class tbl ON tbl.oid = i.indrelid
            WHERE idx.relnamespace = target.oid
        ),
        'functions', (
            SELECT jsonb_agg(jsonb_build_object(
                'name', p.proname, 'owner', p.proowner::regrole::text,
                'acl', p.proacl::text, 'securityDefiner', p.prosecdef,
                'configuration', p.proconfig::text,
                'definition', pg_get_functiondef(p.oid)
            ) ORDER BY p.proname, pg_get_function_identity_arguments(p.oid))
            FROM pg_proc p WHERE p.pronamespace = target.oid
        ),
        'triggers', (
            SELECT jsonb_agg(pg_get_triggerdef(t.oid) ORDER BY c.relname, t.tgname)
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
            WHERE c.relnamespace = target.oid AND NOT t.tgisinternal
        ),
        'policies', (
            SELECT jsonb_agg(pol.polname ORDER BY c.relname, pol.polname)
            FROM pg_policy pol JOIN pg_class c ON c.oid = pol.polrelid
            WHERE c.relnamespace = target.oid
        ),
        'rules', (
            SELECT jsonb_agg(r.rulename ORDER BY c.relname, r.rulename)
            FROM pg_rewrite r JOIN pg_class c ON c.oid = r.ev_class
            WHERE c.relnamespace = target.oid AND r.rulename <> '_RETURN'
        ),
        'defaultAcls', (
            SELECT jsonb_agg(da.defaclobjtype::text || ':' || da.defaclacl::text ORDER BY da.defaclobjtype)
            FROM pg_default_acl da WHERE da.defaclnamespace = target.oid
        )
    ) AS catalog FROM target
)
SELECT encode(sha256(convert_to(catalog::text, 'UTF8')), 'hex') FROM objects;
