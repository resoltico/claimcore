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
SELECT case_id::text AS case1,case_reference AS issued FROM claimcore.cases \gset
SELECT pg_temp.expect('erasure requires an accepted ERASE',
    format('SELECT claimcore.clear_owner(''FAMILY'',%L)', :'case1'), 'CC002');

SELECT pg_temp.expect('reference differing only by ASCII case refused',
    format($s$INSERT INTO claimcore.cases (case_id,case_reference,revision,state,snapshot,status,progress,changed_at,search_text,received_on,claimed_amount,claimed_currency,payable_amount,payable_currency,registered_on,last_activity_on) VALUES ('c0000000-0000-4000-8000-000000000002', %L, 1, 'ACTIVE', '\x01', 'OPENED', 'UNDECIDED', TIMESTAMPTZ '2026-09-01T00:00:00Z', 'synthetic', DATE '2026-08-03', 1, 'EUR', NULL, NULL, DATE '2026-09-01', DATE '2026-09-01')$s$, lower(:'issued' COLLATE "C")), '23505');
SELECT pg_temp.expect('reference with a space refused',
    $s$INSERT INTO claimcore.cases (case_id,case_reference,revision,state,snapshot,status,progress,changed_at,search_text,received_on,claimed_amount,claimed_currency,payable_amount,payable_currency,registered_on,last_activity_on) VALUES ('c0000000-0000-4000-8000-000000000003', 'DEMO 3', 1, 'ACTIVE', '\x01', 'OPENED', 'UNDECIDED', TIMESTAMPTZ '2026-09-01T00:00:00Z', 'synthetic', DATE '2026-08-03', 1, 'EUR', NULL, NULL, DATE '2026-09-01', DATE '2026-09-01')$s$, '23514');
SELECT pg_temp.expect('non-ASCII reference refused',
    $s$INSERT INTO claimcore.cases (case_id,case_reference,revision,state,snapshot,status,progress,changed_at,search_text,received_on,claimed_amount,claimed_currency,payable_amount,payable_currency,registered_on,last_activity_on) VALUES ('c0000000-0000-4000-8000-000000000003', 'LIETA-Ā1', 1, 'ACTIVE', '\x01', 'OPENED', 'UNDECIDED', TIMESTAMPTZ '2026-09-01T00:00:00Z', 'synthetic', DATE '2026-08-03', 1, 'EUR', NULL, NULL, DATE '2026-09-01', DATE '2026-09-01')$s$, '23514');
SELECT pg_temp.expect('received date is optional on a live case',
    $s$UPDATE claimcore.cases SET received_on=NULL$s$, '00000');
SELECT pg_temp.expect('infinite received date refused',
    $s$UPDATE claimcore.cases SET received_on='infinity'$s$, '23514');
SELECT pg_temp.expect('BCE received date refused',
    $s$UPDATE claimcore.cases SET received_on=DATE '0001-01-01 BC'$s$, '23514');
SELECT pg_temp.expect('unknown responsible actor refused',
    $s$UPDATE claimcore.cases SET responsible_actor='c0000000-0000-4000-8000-000000000099'$s$, '23503');
SELECT pg_temp.expect('infinite follow-up refused',
    $s$UPDATE claimcore.cases SET follow_up='infinity'$s$, '23514');
UPDATE claimcore.cases SET follow_up=DATE '2027-12-31';
SELECT pg_temp.expect('erasure must clear follow-up',
    $s$UPDATE claimcore.cases SET state='ERASED',snapshot=NULL,status=NULL,progress=NULL,search_text=NULL,received_on=NULL,claimed_amount=NULL,claimed_currency=NULL,payment_review=NULL,outcome=NULL,payable_amount=NULL,payable_currency=NULL,responsible_actor=NULL,work_search_text=NULL$s$, '23514');
SELECT pg_temp.expect('profile columns cannot be cleared without current accepted erasure',
    format($s$UPDATE claimcore.actors SET display_name=NULL,contact=NULL,display_name_search=NULL,contact_search=NULL WHERE actor_id=%L$s$, :'owner'), 'CC003');
SELECT pg_temp.expect('durable revision exceeds exact JSON integer envelope',
    $s$UPDATE claimcore.cases SET revision=9007199254740992$s$, '23514');
SELECT pg_temp.expect('durable snapshot exceeds byte envelope',
    $s$UPDATE claimcore.cases SET snapshot=decode(repeat('01',32769),'hex')$s$, '23514');
SELECT pg_temp.expect('durable exact numeric precision refuses overflow',
    $s$UPDATE claimcore.cases SET claimed_amount=1000000000000000000$s$, '22003');
SELECT pg_temp.expect('retained exact negative amount remains structurally representable',
    $s$UPDATE claimcore.cases SET claimed_amount=-999999999999999999.9999$s$, '00000');
SELECT pg_temp.expect('money sign is domain admission rather than structural SQL policy',
    $s$UPDATE claimcore.cases SET claimed_amount=-1$s$, '00000');
SELECT pg_temp.expect('lowercase claimed currency refused',
    $s$UPDATE claimcore.cases SET claimed_currency='eur'$s$, '23514');
SELECT pg_temp.expect('payable amount without currency refused',
    $s$UPDATE claimcore.cases SET payable_amount=1$s$, '23514');
SELECT pg_temp.expect('payable currency without amount refused',
    $s$UPDATE claimcore.cases SET payable_currency='EUR'$s$, '23514');
SELECT pg_temp.expect('domain lifecycle tokens do not independently decide SQL clearing',
    $s$UPDATE claimcore.cases SET state='ERASED'$s$, '00000');
SELECT pg_temp.expect('live projection without snapshot refused',
    $s$UPDATE claimcore.cases SET snapshot=NULL$s$, '23514');
SELECT pg_temp.expect('empty snapshot refused',
    $s$UPDATE claimcore.cases SET snapshot=''::bytea$s$, '23514');
SELECT pg_temp.expect('oversized snapshot refused',
    $s$UPDATE claimcore.cases SET snapshot=decode(repeat('00',32769),'hex')$s$, '23514');
SELECT pg_temp.expect('projection revision beyond 2^53-1 refused',
    $s$UPDATE claimcore.cases SET revision=9007199254740992$s$, '23514');
SELECT pg_temp.expect('projection clearing requires accepted matching erasure evidence',
    $s$UPDATE claimcore.cases SET follow_up=NULL,deadline_due=NULL,responsible_actor=NULL,revision=3,snapshot=NULL,status=NULL,progress=NULL,search_text=NULL,received_on=NULL,claimed_amount=NULL,claimed_currency=NULL,payable_amount=NULL,payable_currency=NULL,payment_review=NULL,outcome=NULL,work_search_text=NULL$s$, 'CC003');
SELECT snapshot IS NOT NULL AND received_on IS NOT NULL AND claimed_amount IS NOT NULL AND claimed_currency IS NOT NULL AND case_reference=:'issued' AS retained_projection FROM claimcore.cases WHERE case_id=:'case1' \gset
\if :retained_projection
\echo PASS unmatched erasure leaves private projections intact
\else
\echo FAIL unmatched erasure cleared private projections
\quit 3
\endif

-- People projection constraints retain effect provenance.
SELECT pg_temp.expect('malformed role token refused',
    format($s$INSERT INTO claimcore.grants(granted_sequence,granted_effect_index,actor_id,role,case_id) VALUES (1,0,%L,'invalid-role',NULL)$s$, :'owner'), '23514');
SELECT pg_temp.expect('duplicate installation grant refused',
    format($s$INSERT INTO claimcore.grants(granted_sequence,granted_effect_index,actor_id,role,case_id) VALUES (1,0,%L,'OWNER',NULL)$s$, :'owner'), '23505');
\echo PASS runtime scenarios complete
