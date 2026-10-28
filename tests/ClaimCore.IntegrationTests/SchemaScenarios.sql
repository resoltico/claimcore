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

\set h0 '''\\x0000000000000000000000000000000000000000000000000000000000000000'''
\set h1 '''\\x1111111111111111111111111111111111111111111111111111111111111111'''
\set h2 '''\\x2222222222222222222222222222222222222222222222222222222222222222'''
\set h3 '''\\x3333333333333333333333333333333333333333333333333333333333333333'''
\set hx '''\\x9999999999999999999999999999999999999999999999999999999999999999'''
\set n32 '''\\xabababababababababababababababababababababababababababababababab'''
\set body '''\\x000000017b0000000000'''
\set owner '''a0000000-0000-4000-8000-000000000001'''
\set case1 '''c0000000-0000-4000-8000-000000000001'''

-- Ledger linkage, shape and identity --------------------------------------------------------
INSERT INTO claimcore.ledger VALUES (1, :h0, :h1, 1, 'AUTHORITY', 'PROVISION_OWNER', :owner,
    :owner, clock_timestamp(), :owner, NULL, NULL, :h3, :body, :n32);
SELECT pg_temp.expect('gap refused',
    format($s$INSERT INTO claimcore.ledger VALUES (3, %L, %L, 1, 'CASE', 'OPEN',
      '10000000-0000-4000-8000-000000000003', %L, clock_timestamp(), %L, 1, current_date,
      %L, %L, %L)$s$, :h1, :hx, :owner, :case1, :h3, :body, :n32), 'CC001');
SELECT pg_temp.expect('wrong previous hash refused',
    format($s$INSERT INTO claimcore.ledger VALUES (2, %L, %L, 1, 'CASE', 'OPEN',
      '10000000-0000-4000-8000-000000000002', %L, clock_timestamp(), %L, 1, current_date,
      %L, %L, %L)$s$, :hx, :hx, :owner, :case1, :h3, :body, :n32), 'CC001');
INSERT INTO claimcore.ledger VALUES (2, :h1, :h2, 1, 'CASE', 'OPEN',
    '10000000-0000-4000-8000-000000000002', :owner, clock_timestamp(), :case1, 1, current_date,
    :h3, :body, :n32);
SELECT pg_temp.expect('duplicate operation refused',
    format($s$INSERT INTO claimcore.ledger VALUES (3, %L, %L, 1, 'CASE', 'CLOSE',
      '10000000-0000-4000-8000-000000000002', %L, clock_timestamp(), %L, 2, current_date,
      %L, %L, %L)$s$, :h2, :hx, :owner, :case1, :h3, :body, :n32), '23505');
SELECT pg_temp.expect('duplicate case revision refused',
    format($s$INSERT INTO claimcore.ledger VALUES (3, %L, %L, 1, 'CASE', 'CLOSE',
      '10000000-0000-4000-8000-000000000009', %L, clock_timestamp(), %L, 1, current_date,
      %L, %L, %L)$s$, :h2, :hx, :owner, :case1, :h3, :body, :n32), '23505');
SELECT pg_temp.expect('case row without business date refused',
    format($s$INSERT INTO claimcore.ledger VALUES (3, %L, %L, 1, 'CASE', 'CLOSE',
      '10000000-0000-4000-8000-000000000004', %L, clock_timestamp(), %L, 2, NULL,
      %L, %L, %L)$s$, :h2, :hx, :owner, :case1, :h3, :body, :n32), '23514');
SELECT pg_temp.expect('authority row without payload refused',
    format($s$INSERT INTO claimcore.ledger VALUES (3, %L, %L, 1, 'AUTHORITY', 'GRANT',
      '10000000-0000-4000-8000-000000000004', %L, clock_timestamp(), %L, NULL, NULL,
      %L, NULL, NULL)$s$, :h2, :hx, :owner, :owner, :h3), '23514');
SELECT pg_temp.expect('malformed authority act token refused',
    format($s$INSERT INTO claimcore.ledger VALUES (3, %L, %L, 1, 'AUTHORITY', 'invalid',
      '10000000-0000-4000-8000-000000000004', %L, clock_timestamp(), %L, NULL, NULL,
      %L, %L, %L)$s$, :h2, :hx, :owner, :owner, :h3, :body, :n32), '23514');
SELECT pg_temp.expect('payload without nonce refused',
    format($s$INSERT INTO claimcore.ledger VALUES (3, %L, %L, 1, 'CASE', 'CLOSE',
      '10000000-0000-4000-8000-000000000004', %L, clock_timestamp(), %L, 2, current_date,
      %L, %L, NULL)$s$, :h2, :hx, :owner, :case1, :h3, :body), '23514');
SELECT pg_temp.expect('revision beyond 2^53-1 refused',
    format($s$INSERT INTO claimcore.ledger VALUES (3, %L, %L, 1, 'CASE', 'CLOSE',
      '10000000-0000-4000-8000-000000000004', %L, clock_timestamp(), %L, 9007199254740992,
      current_date, %L, %L, %L)$s$, :h2, :hx, :owner, :case1, :h3, :body, :n32), '23514');
SELECT pg_temp.expect('earlier entry format refused',
    format($s$INSERT INTO claimcore.ledger VALUES (3, %L, %L, 3, 'CASE', 'CLOSE',
      '10000000-0000-4000-8000-000000000004', %L, clock_timestamp(), %L, 2, current_date,
      %L, %L, %L)$s$, :h2, :hx, :owner, :case1, :h3, :body, :n32), '23514');

-- Runtime privileges --------------------------------------------------------------------------
SELECT pg_temp.expect('runtime cannot update a ledger row',
    'UPDATE claimcore.ledger SET payload = NULL, payload_nonce = NULL WHERE sequence = 2', '42501');
SELECT pg_temp.expect('runtime cannot delete a ledger row',
    'DELETE FROM claimcore.ledger WHERE sequence = 2', '42501');
SELECT pg_temp.expect('runtime cannot move the head directly',
    'UPDATE claimcore.ledger_head SET sequence = 0', '42501');
SELECT pg_temp.expect('runtime cannot run DDL',
    'CREATE TABLE claimcore.extra (x int)', '42501');

-- Erasure -------------------------------------------------------------------------------------
SELECT pg_temp.expect('erasure before an accepted ERASE refused',
    format('SELECT claimcore.erase_case_payload(%L)', :case1), 'CC002');
INSERT INTO claimcore.ledger VALUES (3, :h2, :h3, 1, 'CASE', 'ERASE',
    '10000000-0000-4000-8000-000000000005', :owner, clock_timestamp(), :case1, 2, current_date,
    :h3, :body, :n32);
SELECT claimcore.erase_case_payload(:case1) = 2 AS erased_two_case_payloads \gset
\if :erased_two_case_payloads
\echo PASS erasure clears both case payloads
\else
\echo FAIL erasure did not clear exactly two payloads
\quit 3
\endif
SELECT count(*) = 1 AS authority_payload_kept FROM claimcore.ledger
 WHERE subject_kind = 'AUTHORITY' AND payload IS NOT NULL \gset
\if :authority_payload_kept
\echo PASS authority payload untouched by case erasure
\else
\echo FAIL authority payload changed
\quit 3
\endif

-- Snapshot projection invariants ---------------------------------------------------------------
INSERT INTO claimcore.cases VALUES (:case1, 'Demo-0001', 1, 'ACTIVE', '\x01');
SELECT pg_temp.expect('reference differing only by ASCII case refused',
    $s$INSERT INTO claimcore.cases VALUES ('c0000000-0000-4000-8000-000000000002', 'DEMO-0001', 1, 'ACTIVE', '\x01')$s$, '23505');
SELECT pg_temp.expect('reference with a space refused',
    $s$INSERT INTO claimcore.cases VALUES ('c0000000-0000-4000-8000-000000000003', 'DEMO 3', 1, 'ACTIVE', '\x01')$s$, '23514');
SELECT pg_temp.expect('non-ASCII reference refused',
    $s$INSERT INTO claimcore.cases VALUES ('c0000000-0000-4000-8000-000000000003', 'LIETA-Ā1', 1, 'ACTIVE', '\x01')$s$, '23514');
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
UPDATE claimcore.cases SET revision=3,state='ERASED',snapshot=NULL WHERE case_id=:case1;
SELECT snapshot IS NULL AND case_reference='Demo-0001' AND revision=3 AND state='ERASED' AS erased_projection FROM claimcore.cases WHERE case_id=:case1 \gset
\if :erased_projection
\echo PASS erased projection keeps only reference, revision and state
\else
\echo FAIL erased projection retained snapshot bytes
\quit 3
\endif

-- Authority projections -----------------------------------------------------------------------
INSERT INTO claimcore.actors VALUES (:owner, 'HUMAN', 'https://issuer.example/', 'owner', true);
INSERT INTO claimcore.grants VALUES (:owner, 'OWNER', NULL);
INSERT INTO claimcore.grants VALUES (:owner, 'PAYMENT_RECORDER', :case1);
SELECT pg_temp.expect('malformed role token refused',
    format($s$INSERT INTO claimcore.grants VALUES (%L, 'invalid-role', NULL)$s$, :owner), '23514');
SELECT pg_temp.expect('duplicate installation grant refused',
    format($s$INSERT INTO claimcore.grants VALUES (%L, 'OWNER', NULL)$s$, :owner), '23505');
\echo PASS runtime scenarios complete
