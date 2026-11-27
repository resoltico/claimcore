-- Install as the register cluster administrator. Only the register owner can read
-- the current timeline's history. The function takes no filename or other input.
CREATE SCHEMA claimcore_restore_provider;
REVOKE ALL ON SCHEMA claimcore_restore_provider FROM PUBLIC;
GRANT USAGE ON SCHEMA claimcore_restore_provider TO claimcore_owner;

CREATE FUNCTION claimcore_restore_provider.current_timeline_history()
RETURNS text LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
    SELECT pg_catalog.pg_read_file(
        'pg_wal/' || pg_catalog.left(pg_catalog.pg_walfile_name(pg_catalog.pg_current_wal_insert_lsn()), 8) || '.history',
        0, 65537, true)
    WHERE NOT pg_catalog.pg_is_in_recovery()
$$;

REVOKE ALL ON FUNCTION claimcore_restore_provider.current_timeline_history() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION claimcore_restore_provider.current_timeline_history() TO claimcore_owner;
