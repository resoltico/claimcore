-- Owner-level append-only guards, executed by SchemaTests after the runtime scenarios.
\set ON_ERROR_STOP 1
\set QUIET 1

CREATE FUNCTION pg_temp.expect(label text, statement text, expected text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    BEGIN
        EXECUTE statement;
    EXCEPTION WHEN OTHERS THEN
        IF SQLSTATE = expected THEN
            RAISE NOTICE 'PASS % (%)', label, expected;
            RETURN;
        END IF;
        RAISE EXCEPTION 'FAIL % expected % got %: %', label, expected, SQLSTATE, SQLERRM;
    END;
    RAISE EXCEPTION 'FAIL % expected % but the statement succeeded', label, expected;
END;
$$;

SELECT pg_temp.expect('owner cannot rewrite ledger metadata',
    $s$UPDATE claimcore.ledger SET actor_id = 'a0000000-0000-4000-8000-000000000009'
       WHERE sequence = 2$s$, 'CC003');
SELECT pg_temp.expect('owner cannot restore an erased payload',
    $s$UPDATE claimcore.ledger SET payload = '\x00', payload_nonce = NULL WHERE sequence = 2$s$,
    'CC003');
SELECT pg_temp.expect('owner cannot clear an authority payload',
    $s$UPDATE claimcore.ledger SET payload = NULL, payload_nonce = NULL WHERE sequence = 1$s$,
    'CC003');
SELECT pg_temp.expect('owner cannot delete a ledger row',
    'DELETE FROM claimcore.ledger WHERE sequence = 3', 'CC003');
SELECT pg_temp.expect('owner cannot truncate the ledger',
    'TRUNCATE claimcore.ledger CASCADE', 'CC003');
SELECT (SELECT sequence FROM claimcore.ledger_head) = 3
   AND (SELECT entry_hash FROM claimcore.ledger_head)
       = (SELECT entry_hash FROM claimcore.ledger ORDER BY sequence DESC LIMIT 1)
   AS head_matches \gset
\if :head_matches
\echo PASS head equals the last ledger row
\else
\echo FAIL head differs from the last ledger row
\quit 3
\endif
\echo PASS owner scenarios complete
