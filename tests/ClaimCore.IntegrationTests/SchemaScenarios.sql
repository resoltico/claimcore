-- Runtime schema scenarios for db/baseline.sql. Executed by SchemaTests.
-- Each pg_temp.expect call runs one statement in a subtransaction and requires its SQLSTATE.
-- Plain statements must succeed. The script prints one PASS line per expectation.
\set ON_ERROR_STOP 1
\set QUIET 1

CREATE FUNCTION pg_temp.expect(label text, statement text, expected text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    BEGIN
        EXECUTE statement;
        SET CONSTRAINTS ALL IMMEDIATE;
        IF expected = '00000' THEN
            RAISE EXCEPTION USING ERRCODE = 'ZX001', MESSAGE = 'rollback successful witness';
        END IF;
    EXCEPTION WHEN OTHERS THEN
        IF SQLSTATE = expected OR (expected = '00000' AND SQLSTATE = 'ZX001') THEN
            RAISE NOTICE 'PASS % (%)', label, expected;
            RETURN;
        END IF;
        RAISE EXCEPTION 'FAIL % expected % got %: %', label, expected, SQLSTATE, SQLERRM;
    END;
    RAISE EXCEPTION 'FAIL % expected % but the statement succeeded', label, expected;
END;
$$;

-- The F# fixture seeds real accepted roots through the production service.
SELECT actor_id::text AS owner FROM claimcore.actors WHERE subject='synthetic-owner' \gset
SELECT case_id::text AS case1 FROM claimcore.cases WHERE case_reference='synthetic-a' \gset
SELECT pg_temp.expect('erasure requires an accepted ERASE',
    format('SELECT claimcore.erase_case_payload(%L)', :'case1'), 'CC002');

SELECT pg_temp.expect('reference differing only by ASCII case refused',
    $s$INSERT INTO claimcore.cases (case_id,case_reference,revision,state,snapshot,status,progress,changed_at,search_text,notified_on,claimed_amount,claimed_currency,payable_amount,payable_currency) VALUES ('c0000000-0000-4000-8000-000000000002', 'SYNTHETIC-A', 1, 'ACTIVE', '\x01', 'OPENED', 'UNDECIDED', TIMESTAMPTZ '2026-09-01T00:00:00Z', 'synthetic', DATE '2026-08-03', 1, 'EUR', NULL, NULL)$s$, '23505');
SELECT pg_temp.expect('reference with a space refused',
    $s$INSERT INTO claimcore.cases (case_id,case_reference,revision,state,snapshot,status,progress,changed_at,search_text,notified_on,claimed_amount,claimed_currency,payable_amount,payable_currency) VALUES ('c0000000-0000-4000-8000-000000000003', 'DEMO 3', 1, 'ACTIVE', '\x01', 'OPENED', 'UNDECIDED', TIMESTAMPTZ '2026-09-01T00:00:00Z', 'synthetic', DATE '2026-08-03', 1, 'EUR', NULL, NULL)$s$, '23514');
SELECT pg_temp.expect('non-ASCII reference refused',
    $s$INSERT INTO claimcore.cases (case_id,case_reference,revision,state,snapshot,status,progress,changed_at,search_text,notified_on,claimed_amount,claimed_currency,payable_amount,payable_currency) VALUES ('c0000000-0000-4000-8000-000000000003', 'LIETA-Ā1', 1, 'ACTIVE', '\x01', 'OPENED', 'UNDECIDED', TIMESTAMPTZ '2026-09-01T00:00:00Z', 'synthetic', DATE '2026-08-03', 1, 'EUR', NULL, NULL)$s$, '23514');
SELECT pg_temp.expect('live notification required',
    $s$UPDATE claimcore.cases SET notified_on=NULL$s$, '23514');
SELECT pg_temp.expect('infinite notification refused',
    $s$UPDATE claimcore.cases SET notified_on='infinity'$s$, '23514');
SELECT pg_temp.expect('BCE notification refused',
    $s$UPDATE claimcore.cases SET notified_on=DATE '0001-01-01 BC'$s$, '23514');
SELECT pg_temp.expect('unknown responsible actor refused',
    $s$UPDATE claimcore.cases SET responsible_actor='c0000000-0000-4000-8000-000000000099'$s$, '23503');
SELECT pg_temp.expect('infinite follow-up refused',
    $s$UPDATE claimcore.cases SET follow_up='infinity'$s$, '23514');
UPDATE claimcore.cases SET follow_up=DATE '2027-12-31';
SELECT pg_temp.expect('erasure must clear follow-up',
    $s$UPDATE claimcore.cases SET state='ERASED',snapshot=NULL,status=NULL,progress=NULL,search_text=NULL,notified_on=NULL,claimed_amount=NULL,claimed_currency=NULL$s$, '23514');
SELECT pg_temp.expect('negative claimed amount refused',
    $s$UPDATE claimcore.cases SET claimed_amount=-1$s$, '23514');
SELECT pg_temp.expect('lowercase claimed currency refused',
    $s$UPDATE claimcore.cases SET claimed_currency='eur'$s$, '23514');
SELECT pg_temp.expect('payable amount without currency refused',
    $s$UPDATE claimcore.cases SET payable_amount=1$s$, '23514');
SELECT pg_temp.expect('payable currency without amount refused',
    $s$UPDATE claimcore.cases SET payable_currency='EUR'$s$, '23514');
SELECT pg_temp.expect('erased projection with snapshot refused',
    $s$UPDATE claimcore.cases SET state='ERASED'$s$, '23514');
SELECT pg_temp.expect('live projection without snapshot refused',
    $s$UPDATE claimcore.cases SET snapshot=NULL$s$, '23514');
SELECT pg_temp.expect('empty snapshot refused',
    $s$UPDATE claimcore.cases SET snapshot=''::bytea$s$, '23514');
SELECT pg_temp.expect('oversized snapshot refused',
    $s$UPDATE claimcore.cases SET snapshot=decode(repeat('00',32769),'hex')$s$, '23514');
SELECT pg_temp.expect('projection revision beyond 2^53-1 refused',
    $s$UPDATE claimcore.cases SET revision=9007199254740992$s$, '23514');
UPDATE claimcore.cases SET follow_up=NULL,responsible_actor=NULL,revision=3,state='ERASED',snapshot=NULL,status=NULL,progress=NULL,search_text=NULL,notified_on=NULL,claimed_amount=NULL,claimed_currency=NULL,payable_amount=NULL,payable_currency=NULL,changed_at=TIMESTAMPTZ '2026-09-02T00:00:00Z' WHERE case_id=:'case1';
SELECT snapshot IS NULL AND notified_on IS NULL AND claimed_amount IS NULL AND claimed_currency IS NULL AND payable_amount IS NULL AND payable_currency IS NULL AND case_reference='synthetic-a' AND revision=3 AND state='ERASED' AS erased_projection FROM claimcore.cases WHERE case_id=:'case1' \gset
\if :erased_projection
\echo PASS erased projection keeps only reference, revision and state
\else
\echo FAIL erased projection retained snapshot bytes
\quit 3
\endif

-- Authority projection constraints retain effect provenance.
SELECT pg_temp.expect('malformed role token refused',
    format($s$INSERT INTO claimcore.grants(granted_sequence,granted_effect_index,actor_id,role,case_id) VALUES (1,0,%L,'invalid-role',NULL)$s$, :'owner'), '23514');
SELECT pg_temp.expect('duplicate installation grant refused',
    format($s$INSERT INTO claimcore.grants(granted_sequence,granted_effect_index,actor_id,role,case_id) VALUES (1,0,%L,'OWNER',NULL)$s$, :'owner'), '23505');
\echo PASS runtime scenarios complete
