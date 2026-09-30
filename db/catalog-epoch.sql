-- Catalog change token.
--
-- Parameter: @schema, the namespace whose catalog state is fingerprinted (the primary schema or
-- the witness schema). One statement, one round trip, read-only, no side effects.
--
-- The result is a digest of, for every catalog the admission checks read, the number of rows in
-- scope and a sum of hashes of each row's identity and version (xmin and ctid). Any committed
-- catalog write in scope (DDL, GRANT or REVOKE, an ownership change, a rename, or a direct
-- superuser UPDATE) writes a new row version or changes a row count, so the digest changes.
-- Rolling back changes nothing. VACUUM, ANALYZE and hint bits touch neither xmin nor ctid.
--
-- Objects that belong to the namespace are found by their object id being at least 16384, the first
-- id PostgreSQL hands to objects created after initdb, so the large catalogs are read through their
-- oid index instead of scanned. Every object a user creates (a table, function, type, operator,
-- collation, constraint) has such an id; rows PostgreSQL itself installed are never in the
-- namespace. A row written directly into a catalog with an explicit id below that bound is the one
-- case this cannot see, and the forced periodic full verification is what bounds it.
--
-- The digest is a proof of "unchanged", never of "correct": a caller that has already proven the
-- catalog correct may skip the expensive structural checks while the digest stays the same, and
-- must run them whenever it differs. Over-approximation only costs a full verification; a missed
-- change would be unsound, so the sources below are held against everything the checks read by
-- tests (see CatalogEpochTests). Role attributes live in pg_authid, whose row versions the
-- application role cannot read, so pg_roles is hashed by content instead. The server identity
-- (start time and database) makes a restored or swapped server produce a different digest.
WITH ns AS (
    SELECT oid, nspowner FROM pg_catalog.pg_namespace WHERE nspname = @schema
), rel AS (
    SELECT c.oid FROM pg_catalog.pg_class c WHERE c.oid >= 16384 AND c.relnamespace IN (SELECT oid FROM ns)
), sources(source, row_count, digest) AS (
    SELECT 'roles', count(*), coalesce(sum(pg_catalog.hashtextextended(r::text, 0)), 0)
        FROM pg_catalog.pg_roles r
    UNION ALL
    SELECT 'server', 1, pg_catalog.hashtextextended(
        pg_catalog.pg_postmaster_start_time()::text || ':' || pg_catalog.current_database(), 0)
    UNION ALL
    -- The namespace itself: owner, ACL, name.
    SELECT 'namespace', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_namespace x WHERE x.oid IN (SELECT oid FROM ns)
    ) q
    UNION ALL
    -- The current database row: owner and database-level ACL.
    SELECT 'database', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_database x WHERE x.datname = pg_catalog.current_database()
    ) q
    UNION ALL
    -- Every role membership, which changes what has_*_privilege() answers.
    SELECT 'membership', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_auth_members x
    ) q
    UNION ALL
    -- Relations, indexes and sequences: owner, ACL, options, persistence, row security, subclass flag.
    SELECT 'class', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.relhassubclass::text||x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_class x WHERE x.oid >= 16384 AND x.relnamespace IN (SELECT oid FROM ns)
    ) q
    UNION ALL
    -- Columns: name, type, nullability, storage, column ACL.
    SELECT 'attribute', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.attrelid::bigint*4096+x.attnum)), 0) FROM pg_catalog.pg_attribute x WHERE x.attrelid IN (SELECT oid FROM rel)
    ) q
    UNION ALL
    -- Column defaults.
    SELECT 'attrdef', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_attrdef x WHERE x.adrelid IN (SELECT oid FROM rel)
    ) q
    UNION ALL
    -- Constraints, including validation and enforcement.
    SELECT 'constraint', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_constraint x WHERE x.oid >= 16384 AND x.connamespace IN (SELECT oid FROM ns)
    ) q
    UNION ALL
    -- Index definitions and validity flags.
    SELECT 'index', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.indexrelid::bigint)), 0) FROM pg_catalog.pg_index x WHERE x.indexrelid IN (SELECT oid FROM rel)
    ) q
    UNION ALL
    -- Sequence parameters.
    SELECT 'sequence', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.seqrelid::bigint)), 0) FROM pg_catalog.pg_sequence x WHERE x.seqrelid IN (SELECT oid FROM rel)
    ) q
    UNION ALL
    -- Types in the namespace and types its columns use.
    SELECT 'type', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_type x WHERE x.oid IN (SELECT t.oid FROM pg_catalog.pg_type t WHERE t.oid >= 16384 AND t.typnamespace IN (SELECT oid FROM ns) UNION SELECT a.atttypid FROM pg_catalog.pg_attribute a WHERE a.attrelid IN (SELECT oid FROM rel))
    ) q
    UNION ALL
    -- Functions: body, owner, ACL, security-definer flag, configuration.
    SELECT 'proc', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_proc x WHERE x.oid >= 16384 AND x.pronamespace IN (SELECT oid FROM ns)
    ) q
    UNION ALL
    -- Triggers, including internal foreign-key triggers.
    SELECT 'trigger', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_trigger x WHERE x.tgrelid IN (SELECT oid FROM rel)
    ) q
    UNION ALL
    -- Rules and view definitions.
    SELECT 'rewrite', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_rewrite x WHERE x.ev_class IN (SELECT oid FROM rel)
    ) q
    UNION ALL
    -- Row-level-security policies.
    SELECT 'policy', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_policy x WHERE x.polrelid IN (SELECT oid FROM rel)
    ) q
    UNION ALL
    -- Default privileges the namespace owner set for the namespace or globally.
    SELECT 'default_acl', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_default_acl x WHERE x.defaclrole IN (SELECT nspowner FROM ns) AND x.defaclnamespace IN (0, (SELECT oid FROM ns))
    ) q
    UNION ALL
    -- Collations in the namespace and collations its columns and types use.
    SELECT 'collation', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_collation x WHERE x.oid IN (SELECT c.oid FROM pg_catalog.pg_collation c WHERE c.oid >= 16384 AND c.collnamespace IN (SELECT oid FROM ns) UNION SELECT a.attcollation FROM pg_catalog.pg_attribute a WHERE a.attrelid IN (SELECT oid FROM rel) UNION SELECT t.typcollation FROM pg_catalog.pg_type t WHERE t.typnamespace IN (SELECT oid FROM ns))
    ) q
    UNION ALL
    -- Operators in the namespace.
    SELECT 'operator', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_operator x WHERE x.oid >= 16384 AND x.oprnamespace IN (SELECT oid FROM ns)
    ) q
    UNION ALL
    -- Operator classes in the namespace and those its indexes use.
    SELECT 'opclass', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_opclass x WHERE x.oid IN (SELECT c.oid FROM pg_catalog.pg_opclass c WHERE c.oid >= 16384 AND c.opcnamespace IN (SELECT oid FROM ns) UNION SELECT pg_catalog.unnest(i.indclass) FROM pg_catalog.pg_index i WHERE i.indexrelid IN (SELECT oid FROM rel))
    ) q
    UNION ALL
    -- Operator families in the namespace.
    SELECT 'opfamily', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_opfamily x WHERE x.oid >= 16384 AND x.opfnamespace IN (SELECT oid FROM ns)
    ) q
    UNION ALL
    -- Encoding conversions in the namespace.
    SELECT 'conversion', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_conversion x WHERE x.oid >= 16384 AND x.connamespace IN (SELECT oid FROM ns)
    ) q
    UNION ALL
    -- Text-search configurations in the namespace.
    SELECT 'ts_config', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_ts_config x WHERE x.cfgnamespace IN (SELECT oid FROM ns)
    ) q
    UNION ALL
    -- Text-search dictionaries in the namespace.
    SELECT 'ts_dict', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_ts_dict x WHERE x.dictnamespace IN (SELECT oid FROM ns)
    ) q
    UNION ALL
    -- Text-search parsers in the namespace.
    SELECT 'ts_parser', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_ts_parser x WHERE x.prsnamespace IN (SELECT oid FROM ns)
    ) q
    UNION ALL
    -- Text-search templates in the namespace.
    SELECT 'ts_template', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_ts_template x WHERE x.tmplnamespace IN (SELECT oid FROM ns)
    ) q
    UNION ALL
    -- Extensions installed into the namespace.
    SELECT 'extension', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_extension x WHERE x.extnamespace IN (SELECT oid FROM ns)
    ) q
    UNION ALL
    -- Table inheritance touching the namespace.
    SELECT 'inherits', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.inhrelid::bigint*4294967296+x.inhparent::bigint)), 0) FROM pg_catalog.pg_inherits x WHERE x.inhrelid IN (SELECT oid FROM rel) OR x.inhparent IN (SELECT oid FROM rel)
    ) q
    UNION ALL
    -- Table and index access methods.
    SELECT 'access_method', * FROM (
        SELECT count(*), coalesce(sum(pg_catalog.hashtextextended(x.xmin::text||x.ctid::text, x.oid::bigint)), 0) FROM pg_catalog.pg_am x
    ) q
)
SELECT pg_catalog.encode(
    pg_catalog.sha256(pg_catalog.convert_to(
        pg_catalog.string_agg(source || '=' || row_count::text || '/' || digest::text, ';' ORDER BY source),
        'UTF8')),
    'hex')
FROM sources;
