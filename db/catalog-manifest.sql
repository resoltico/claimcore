-- Canonical, read-only PostgreSQL 18 catalog projection for the ClaimCore namespace.
-- Run with search_path=pg_catalog on a pristine installation and on every admission.
WITH target AS (
    SELECT oid, nspowner, nspacl
    FROM pg_catalog.pg_namespace
    WHERE nspname = 'claimcore'
), objects AS (
    SELECT 'schema' AS kind, 'claimcore' AS name,
        pg_catalog.jsonb_build_object(
            'owner', '$OWNER', 'aclNull', nspacl IS NULL,
            'acl', (SELECT coalesce(pg_catalog.jsonb_agg(
                pg_catalog.jsonb_build_array(
                    CASE WHEN a.grantee = 0 THEN 'PUBLIC' WHEN a.grantee = t.nspowner THEN '$OWNER'
                         ELSE pg_catalog.pg_get_userbyid(a.grantee) END,
                    CASE WHEN a.grantor = t.nspowner THEN '$OWNER'
                         ELSE pg_catalog.pg_get_userbyid(a.grantor) END,
                    a.privilege_type, a.is_grantable)
                ORDER BY a.grantee, a.grantor, a.privilege_type, a.is_grantable), '[]'::jsonb)
                FROM pg_catalog.aclexplode(t.nspacl) a)) AS details
    FROM target t

    UNION ALL
    SELECT 'relation', c.relname::text,
        pg_catalog.jsonb_build_object(
            'kind', c.relkind::text, 'persistence', c.relpersistence::text,
            'owner', CASE WHEN c.relowner = t.nspowner THEN '$OWNER'
                          ELSE pg_catalog.pg_get_userbyid(c.relowner) END,
            'aclNull', c.relacl IS NULL,
            'acl', (SELECT coalesce(pg_catalog.jsonb_agg(
                pg_catalog.jsonb_build_array(
                    CASE WHEN a.grantee = 0 THEN 'PUBLIC' WHEN a.grantee = t.nspowner THEN '$OWNER'
                         ELSE pg_catalog.pg_get_userbyid(a.grantee) END,
                    CASE WHEN a.grantor = t.nspowner THEN '$OWNER'
                         ELSE pg_catalog.pg_get_userbyid(a.grantor) END,
                    a.privilege_type, a.is_grantable)
                ORDER BY a.grantee, a.grantor, a.privilege_type, a.is_grantable), '[]'::jsonb)
                FROM pg_catalog.aclexplode(c.relacl) a),
            'rowSecurity', c.relrowsecurity, 'forceRowSecurity', c.relforcerowsecurity,
            'replicaIdentity', c.relreplident::text, 'checks', c.relchecks,
            'partition', c.relispartition, 'hasSubclass', c.relhassubclass,
            'accessMethod', am.amname::text, 'options', c.reloptions::text)
    FROM target t JOIN pg_catalog.pg_class c ON c.relnamespace = t.oid
    LEFT JOIN pg_catalog.pg_am am ON am.oid = c.relam

    UNION ALL
    SELECT 'column', c.relname::text || '.' || a.attnum::text,
        pg_catalog.jsonb_build_object(
            'name', a.attname::text, 'number', a.attnum, 'dropped', a.attisdropped,
            'type', CASE WHEN a.attisdropped THEN '<dropped>'
                         ELSE pg_catalog.format_type(a.atttypid, a.atttypmod) END,
            'typeSchema', tn.nspname::text, 'typeName', ty.typname::text,
            'notNull', a.attnotnull, 'identity', a.attidentity::text,
            'generated', a.attgenerated::text, 'storage', a.attstorage::text,
            'compression', a.attcompression::text, 'local', a.attislocal,
            'inheritCount', a.attinhcount, 'hasMissing', a.atthasmissing,
            'missingValue', a.attmissingval::text,
            'collation', CASE WHEN a.attcollation = 0 THEN NULL
                ELSE cn.nspname::text || '.' || co.collname::text END,
            'default', pg_catalog.pg_get_expr(d.adbin, d.adrelid, false),
            'aclNull', a.attacl IS NULL,
            'acl', (SELECT coalesce(pg_catalog.jsonb_agg(
                pg_catalog.jsonb_build_array(
                    CASE WHEN x.grantee = 0 THEN 'PUBLIC' WHEN x.grantee = t.nspowner THEN '$OWNER'
                         ELSE pg_catalog.pg_get_userbyid(x.grantee) END,
                    CASE WHEN x.grantor = t.nspowner THEN '$OWNER'
                         ELSE pg_catalog.pg_get_userbyid(x.grantor) END,
                    x.privilege_type, x.is_grantable)
                ORDER BY x.grantee, x.grantor, x.privilege_type, x.is_grantable), '[]'::jsonb)
                FROM pg_catalog.aclexplode(a.attacl) x))
    FROM target t JOIN pg_catalog.pg_class c ON c.relnamespace = t.oid
    JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0
    LEFT JOIN pg_catalog.pg_type ty ON ty.oid = a.atttypid
    LEFT JOIN pg_catalog.pg_namespace tn ON tn.oid = ty.typnamespace
    LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
    LEFT JOIN pg_catalog.pg_collation co ON co.oid = a.attcollation
    LEFT JOIN pg_catalog.pg_namespace cn ON cn.oid = co.collnamespace
    WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')

    UNION ALL
    SELECT 'constraint', coalesce(c.relname::text, ty.typname::text) || '.' || k.conname::text,
        pg_catalog.jsonb_build_object(
            'kind', k.contype::text, 'definition', pg_catalog.pg_get_constraintdef(k.oid, false),
            'validated', k.convalidated, 'enforced', k.conenforced,
            'deferrable', k.condeferrable, 'deferred', k.condeferred,
            'local', k.conislocal, 'inherited', k.coninhcount, 'noInherit', k.connoinherit,
            'period', k.conperiod, 'columns', k.conkey::text, 'foreignColumns', k.confkey::text,
            'foreignTable', fc.relname::text, 'updateAction', k.confupdtype::text,
            'deleteAction', k.confdeltype::text, 'matchType', k.confmatchtype::text)
    FROM target t JOIN pg_catalog.pg_constraint k ON k.connamespace = t.oid
    LEFT JOIN pg_catalog.pg_class c ON c.oid = k.conrelid
    LEFT JOIN pg_catalog.pg_type ty ON ty.oid = k.contypid
    LEFT JOIN pg_catalog.pg_class fc ON fc.oid = k.confrelid

    UNION ALL
    SELECT 'index', ci.relname::text,
        pg_catalog.jsonb_build_object(
            'table', ct.relname::text, 'definition', pg_catalog.pg_get_indexdef(i.indexrelid),
            'valid', i.indisvalid, 'ready', i.indisready, 'live', i.indislive,
            'checkXmin', i.indcheckxmin, 'clustered', i.indisclustered,
            'replicaIdentity', i.indisreplident, 'exclusion', i.indisexclusion,
            'unique', i.indisunique, 'primary', i.indisprimary,
            'nullsNotDistinct', i.indnullsnotdistinct, 'immediate', i.indimmediate,
            'keyCount', i.indnkeyatts, 'key', i.indkey::text,
            'options', i.indoption::text,
            'collations', (SELECT coalesce(pg_catalog.jsonb_agg(
                CASE WHEN v.oid = 0 THEN NULL ELSE n.nspname::text || '.' || p.collname::text END
                ORDER BY v.position), '[]'::jsonb)
                FROM pg_catalog.unnest(i.indcollation) WITH ORDINALITY v(oid, position)
                LEFT JOIN pg_catalog.pg_collation p ON p.oid = v.oid
                LEFT JOIN pg_catalog.pg_namespace n ON n.oid = p.collnamespace),
            'opclasses', (SELECT coalesce(pg_catalog.jsonb_agg(
                n.nspname::text || '.' || p.opcname::text ORDER BY v.position), '[]'::jsonb)
                FROM pg_catalog.unnest(i.indclass) WITH ORDINALITY v(oid, position)
                JOIN pg_catalog.pg_opclass p ON p.oid = v.oid
                JOIN pg_catalog.pg_namespace n ON n.oid = p.opcnamespace))
    FROM target t JOIN pg_catalog.pg_class ci ON ci.relnamespace = t.oid
    JOIN pg_catalog.pg_index i ON i.indexrelid = ci.oid
    JOIN pg_catalog.pg_class ct ON ct.oid = i.indrelid

    UNION ALL
    SELECT 'sequence', c.relname::text,
        pg_catalog.jsonb_build_object('type', pg_catalog.format_type(s.seqtypid, NULL),
            'start', s.seqstart, 'increment', s.seqincrement, 'min', s.seqmin,
            'max', s.seqmax, 'cache', s.seqcache, 'cycle', s.seqcycle)
    FROM target t JOIN pg_catalog.pg_class c ON c.relnamespace = t.oid
    JOIN pg_catalog.pg_sequence s ON s.seqrelid = c.oid

    UNION ALL
    SELECT 'type', y.typname::text,
        pg_catalog.jsonb_build_object('kind', y.typtype::text, 'category', y.typcategory::text,
            'defined', y.typisdefined, 'notNull', y.typnotnull,
            'default', y.typdefault, 'collation', CASE WHEN y.typcollation = 0 THEN NULL
                ELSE cn.nspname::text || '.' || co.collname::text END,
            'owner', CASE WHEN y.typowner = t.nspowner THEN '$OWNER'
                ELSE pg_catalog.pg_get_userbyid(y.typowner) END,
            'aclNull', y.typacl IS NULL,
            'acl', (SELECT coalesce(pg_catalog.jsonb_agg(
                pg_catalog.jsonb_build_array(
                    CASE WHEN a.grantee = 0 THEN 'PUBLIC' WHEN a.grantee = t.nspowner THEN '$OWNER'
                         ELSE pg_catalog.pg_get_userbyid(a.grantee) END,
                    CASE WHEN a.grantor = t.nspowner THEN '$OWNER'
                         ELSE pg_catalog.pg_get_userbyid(a.grantor) END,
                    a.privilege_type, a.is_grantable)
                ORDER BY a.grantee, a.grantor, a.privilege_type, a.is_grantable), '[]'::jsonb)
                FROM pg_catalog.aclexplode(y.typacl) a))
    FROM target t JOIN pg_catalog.pg_type y ON y.typnamespace = t.oid
    LEFT JOIN pg_catalog.pg_collation co ON co.oid = y.typcollation
    LEFT JOIN pg_catalog.pg_namespace cn ON cn.oid = co.collnamespace

    UNION ALL
    SELECT 'function', p.proname::text || '(' || pg_catalog.pg_get_function_identity_arguments(p.oid) || ')',
        pg_catalog.jsonb_build_object('definition', pg_catalog.pg_get_functiondef(p.oid),
            'kind', p.prokind::text,
            'owner', CASE WHEN p.proowner = t.nspowner THEN '$OWNER'
                ELSE pg_catalog.pg_get_userbyid(p.proowner) END,
            'securityDefiner', p.prosecdef, 'volatility', p.provolatile::text,
            'parallel', p.proparallel::text, 'config', p.proconfig::text,
            'aclNull', p.proacl IS NULL,
            'acl', (SELECT coalesce(pg_catalog.jsonb_agg(
                pg_catalog.jsonb_build_array(
                    CASE WHEN a.grantee = 0 THEN 'PUBLIC' WHEN a.grantee = t.nspowner THEN '$OWNER'
                         ELSE pg_catalog.pg_get_userbyid(a.grantee) END,
                    CASE WHEN a.grantor = t.nspowner THEN '$OWNER'
                         ELSE pg_catalog.pg_get_userbyid(a.grantor) END,
                    a.privilege_type, a.is_grantable)
                ORDER BY a.grantee, a.grantor, a.privilege_type, a.is_grantable), '[]'::jsonb)
                FROM pg_catalog.aclexplode(p.proacl) a))
    FROM target t JOIN pg_catalog.pg_proc p ON p.pronamespace = t.oid

    UNION ALL
    SELECT 'trigger', c.relname::text || '.' ||
        CASE WHEN g.tgisinternal THEN
            k.conname::text || '.' || f.proname::text || '.' || g.tgtype::text
        ELSE g.tgname::text END,
        pg_catalog.jsonb_build_object(
            'definition', CASE WHEN g.tgisinternal THEN NULL
                ELSE pg_catalog.pg_get_triggerdef(g.oid, false) END,
            'enabled', g.tgenabled::text, 'internal', g.tgisinternal,
            'type', g.tgtype, 'function', f.proname::text,
            'constraint', k.conname::text, 'columns', g.tgattr::text,
            'qual', pg_catalog.pg_get_expr(g.tgqual, g.tgrelid, false))
    FROM target t JOIN pg_catalog.pg_class c ON c.relnamespace = t.oid
    JOIN pg_catalog.pg_trigger g ON g.tgrelid = c.oid
    JOIN pg_catalog.pg_proc f ON f.oid = g.tgfoid
    LEFT JOIN pg_catalog.pg_constraint k ON k.oid = g.tgconstraint

    UNION ALL
    SELECT 'rule', c.relname::text || '.' || r.rulename::text,
        pg_catalog.jsonb_build_object('definition', pg_catalog.pg_get_ruledef(r.oid, false),
            'enabled', r.ev_enabled::text)
    FROM target t JOIN pg_catalog.pg_class c ON c.relnamespace = t.oid
    JOIN pg_catalog.pg_rewrite r ON r.ev_class = c.oid

    UNION ALL
    SELECT 'policy', c.relname::text || '.' || p.polname::text,
        pg_catalog.jsonb_build_object('command', p.polcmd::text, 'permissive', p.polpermissive,
            'roles', p.polroles::text, 'qual', pg_catalog.pg_get_expr(p.polqual, p.polrelid, false),
            'withCheck', pg_catalog.pg_get_expr(p.polwithcheck, p.polrelid, false))
    FROM target t JOIN pg_catalog.pg_class c ON c.relnamespace = t.oid
    JOIN pg_catalog.pg_policy p ON p.polrelid = c.oid

    UNION ALL
    SELECT 'defaultAcl', d.defaclobjtype::text || '.' || d.defaclnamespace::text,
        pg_catalog.jsonb_build_object('acl', d.defaclacl::text)
    FROM target t JOIN pg_catalog.pg_default_acl d ON d.defaclrole = t.nspowner
        AND d.defaclnamespace IN (0, t.oid)

    UNION ALL
    SELECT 'collation', c.collname::text, '{}'::jsonb
    FROM target t JOIN pg_catalog.pg_collation c ON c.collnamespace = t.oid

    UNION ALL
    SELECT 'operator', o.oprname::text || '(' || o.oprleft::text || ',' || o.oprright::text || ')',
        '{}'::jsonb
    FROM target t JOIN pg_catalog.pg_operator o ON o.oprnamespace = t.oid

    UNION ALL
    SELECT 'opclass', o.opcname::text, '{}'::jsonb
    FROM target t JOIN pg_catalog.pg_opclass o ON o.opcnamespace = t.oid

    UNION ALL
    SELECT 'opfamily', o.opfname::text, '{}'::jsonb
    FROM target t JOIN pg_catalog.pg_opfamily o ON o.opfnamespace = t.oid

    UNION ALL
    SELECT 'conversion', c.conname::text, '{}'::jsonb
    FROM target t JOIN pg_catalog.pg_conversion c ON c.connamespace = t.oid

    UNION ALL
    SELECT 'tsConfig', c.cfgname::text, '{}'::jsonb
    FROM target t JOIN pg_catalog.pg_ts_config c ON c.cfgnamespace = t.oid

    UNION ALL
    SELECT 'tsDictionary', d.dictname::text, '{}'::jsonb
    FROM target t JOIN pg_catalog.pg_ts_dict d ON d.dictnamespace = t.oid

    UNION ALL
    SELECT 'tsParser', p.prsname::text, '{}'::jsonb
    FROM target t JOIN pg_catalog.pg_ts_parser p ON p.prsnamespace = t.oid

    UNION ALL
    SELECT 'tsTemplate', p.tmplname::text, '{}'::jsonb
    FROM target t JOIN pg_catalog.pg_ts_template p ON p.tmplnamespace = t.oid

    UNION ALL
    SELECT 'extension', e.extname::text, '{}'::jsonb
    FROM target t JOIN pg_catalog.pg_extension e ON e.extnamespace = t.oid

    UNION ALL
    SELECT 'inheritance', child.relname::text || '->' || parent.relname::text,
        pg_catalog.jsonb_build_object('sequence', h.inhseqno,
            'childSchema', childn.nspname::text, 'parentSchema', parentn.nspname::text)
    FROM target t JOIN pg_catalog.pg_inherits h ON true
    JOIN pg_catalog.pg_class child ON child.oid = h.inhrelid
    JOIN pg_catalog.pg_namespace childn ON childn.oid = child.relnamespace
    JOIN pg_catalog.pg_class parent ON parent.oid = h.inhparent
    JOIN pg_catalog.pg_namespace parentn ON parentn.oid = parent.relnamespace
    WHERE child.relnamespace = t.oid OR parent.relnamespace = t.oid
)
SELECT coalesce(pg_catalog.jsonb_agg(
    pg_catalog.jsonb_build_array(kind, name, details) ORDER BY kind, name), '[]'::jsonb)::text
FROM objects;
