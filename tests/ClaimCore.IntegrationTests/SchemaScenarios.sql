\set ON_ERROR_STOP off
\pset format unaligned
\pset tuples_only on
-- helpers (runtime role)
\echo '--- T1 lock head, append seq 1 (OPEN), projection insert'
BEGIN;
SELECT sequence, entry_hash FROM claimcore.lock_ledger_head();
INSERT INTO claimcore.ledger (sequence, previous_hash, entry_hash, entry_format, kind, operation_id, actor_id, recorded_at,
  case_id, case_revision, command_kind, business_date, rule_revision, payload_commitment, payload, payload_nonce, request_sha256)
VALUES (1, decode(repeat('00',32),'hex'), decode(repeat('11',32),'hex'), 1, 'CASE', '10000000-0000-4000-8000-000000000001',
  'a0000000-0000-4000-8000-000000000001', clock_timestamp(), 'c0000000-0000-4000-8000-000000000001', 1, 'OPEN', DATE '2026-08-03', 2,
  decode(repeat('aa',32),'hex'), '\x7b7d', decode(repeat('bb',32),'hex'), decode(repeat('cc',32),'hex'));
INSERT INTO claimcore.cases (case_id, case_reference, revision, disposition, incident_date, incident_notification_date, incident_country,
  claimant_name, insurer_name, claimed_amount, claimed_currency, status)
VALUES ('c0000000-0000-4000-8000-000000000001','DEMO-0001',1,'ACTIVE','2026-08-01','2026-08-03','Lithuania','Example Translation Services Ltd','Example Responsible Insurer',1000.00,'EUR','OPENED');
COMMIT;
SELECT 'head', sequence, encode(entry_hash,'hex') FROM claimcore.ledger_head;
\echo '--- T2 wrong sequence refused (expect CC001)'
INSERT INTO claimcore.ledger (sequence, previous_hash, entry_hash, entry_format, kind, operation_id, actor_id, recorded_at, payload_commitment)
VALUES (5, decode(repeat('11',32),'hex'), decode(repeat('22',32),'hex'), 1, 'AUTHORITY', '10000000-0000-4000-8000-000000000002','a0000000-0000-4000-8000-000000000001', clock_timestamp(), decode(repeat('aa',32),'hex'));
\echo '--- T3 wrong previous hash refused (expect CC001)'
INSERT INTO claimcore.ledger (sequence, previous_hash, entry_hash, entry_format, kind, operation_id, actor_id, recorded_at, payload_commitment)
VALUES (2, decode(repeat('99',32),'hex'), decode(repeat('22',32),'hex'), 1, 'AUTHORITY', '10000000-0000-4000-8000-000000000002','a0000000-0000-4000-8000-000000000001', clock_timestamp(), decode(repeat('aa',32),'hex'));
\echo '--- T4 duplicate operation id refused (expect unique violation 23505)'
INSERT INTO claimcore.ledger (sequence, previous_hash, entry_hash, entry_format, kind, operation_id, actor_id, recorded_at, payload_commitment)
VALUES (2, decode(repeat('11',32),'hex'), decode(repeat('22',32),'hex'), 1, 'AUTHORITY', '10000000-0000-4000-8000-000000000001','a0000000-0000-4000-8000-000000000001', clock_timestamp(), decode(repeat('aa',32),'hex'));
\echo '--- T5 runtime cannot UPDATE / DELETE / TRUNCATE ledger, nor move the head, nor run DDL'
UPDATE claimcore.ledger SET actor_id = actor_id;
DELETE FROM claimcore.ledger;
TRUNCATE claimcore.ledger;
UPDATE claimcore.ledger_head SET sequence = 0;
CREATE TABLE claimcore.x (a int);
ALTER TABLE claimcore.ledger DISABLE TRIGGER ledger_extend_head;
\echo '--- T6 erase refused without ERASE entry (expect CC002)'
SELECT claimcore.erase_case_payload('c0000000-0000-4000-8000-000000000001');
\echo '--- T7 kind/payload shape checks (expect 23514 x2)'
INSERT INTO claimcore.ledger (sequence, previous_hash, entry_hash, entry_format, kind, operation_id, actor_id, recorded_at, payload_commitment, case_id)
VALUES (2, decode(repeat('11',32),'hex'), decode(repeat('22',32),'hex'), 1, 'AUTHORITY', '10000000-0000-4000-8000-000000000003','a0000000-0000-4000-8000-000000000001', clock_timestamp(), decode(repeat('aa',32),'hex'), 'c0000000-0000-4000-8000-000000000001');
INSERT INTO claimcore.ledger (sequence, previous_hash, entry_hash, entry_format, kind, operation_id, actor_id, recorded_at, payload_commitment, payload)
VALUES (2, decode(repeat('11',32),'hex'), decode(repeat('22',32),'hex'), 1, 'AUTHORITY', '10000000-0000-4000-8000-000000000003','a0000000-0000-4000-8000-000000000001', clock_timestamp(), decode(repeat('aa',32),'hex'), '\x00');
SELECT 'head-after-refusals', sequence FROM claimcore.ledger_head;
\echo '--- T8 accepted ERASE entry then erase: payload removed, chain columns intact, projection tombstoned'
BEGIN;
SELECT sequence FROM claimcore.lock_ledger_head();
INSERT INTO claimcore.ledger (sequence, previous_hash, entry_hash, entry_format, kind, operation_id, actor_id, recorded_at,
  case_id, case_revision, command_kind, business_date, rule_revision, payload_commitment, payload, payload_nonce, request_sha256)
VALUES (2, decode(repeat('11',32),'hex'), decode(repeat('22',32),'hex'), 1, 'CASE', '10000000-0000-4000-8000-000000000004',
  'a0000000-0000-4000-8000-000000000001', clock_timestamp(), 'c0000000-0000-4000-8000-000000000001', 2, 'ERASE', DATE '2026-08-04', 2,
  decode(repeat('ab',32),'hex'), '\x7b7d', decode(repeat('bc',32),'hex'), decode(repeat('cd',32),'hex'));
SELECT 'erased-rows', claimcore.erase_case_payload('c0000000-0000-4000-8000-000000000001');
UPDATE claimcore.cases SET disposition='ERASED', revision=2, incident_date=NULL, incident_notification_date=NULL, incident_country=NULL,
  claimant_name=NULL, insurer_name=NULL, claimed_amount=NULL, claimed_currency=NULL, status=NULL WHERE case_id='c0000000-0000-4000-8000-000000000001';
COMMIT;
SELECT sequence, command_kind, payload IS NULL, payload_nonce IS NULL, request_sha256 IS NULL, encode(payload_commitment,'hex')=repeat('aa',32) OR encode(payload_commitment,'hex')=repeat('ab',32) FROM claimcore.ledger ORDER BY sequence;
SELECT case_reference, disposition, revision, claimant_name IS NULL FROM claimcore.cases;
\echo '--- T9 erased row cannot keep business values; active row cannot lose them (expect 23514 x2)'
UPDATE claimcore.cases SET claimant_name='x' WHERE disposition='ERASED';
INSERT INTO claimcore.cases (case_id, case_reference, revision, disposition) VALUES ('c0000000-0000-4000-8000-000000000002','DEMO-0002',1,'ACTIVE');
\echo '--- T10 reference stays reserved after erasure (expect 23505)'
INSERT INTO claimcore.cases (case_id, case_reference, revision, disposition, incident_date, incident_notification_date, incident_country,
  claimant_name, insurer_name, claimed_amount, claimed_currency, status)
VALUES ('c0000000-0000-4000-8000-000000000003','DEMO-0001',1,'ACTIVE','2026-08-01','2026-08-03','Lithuania','A','B',1,'EUR','OPENED');
\echo '--- T11 business CHECKs: paid needs positive decision; chronology (expect 23514 x2)'
INSERT INTO claimcore.cases (case_id, case_reference, revision, disposition, incident_date, incident_notification_date, incident_country,
  claimant_name, insurer_name, claimed_amount, claimed_currency, status, payment_decision_date, payable_amount, payable_currency, payment_date)
VALUES ('c0000000-0000-4000-8000-000000000004','DEMO-0004',1,'ACTIVE','2026-08-01','2026-08-03','Lithuania','A','B',1,'EUR','OPENED','2026-08-05',0,'EUR','2026-08-06');
INSERT INTO claimcore.cases (case_id, case_reference, revision, disposition, incident_date, incident_notification_date, incident_country,
  claimant_name, insurer_name, claimed_amount, claimed_currency, status)
VALUES ('c0000000-0000-4000-8000-000000000005','DEMO-0005',1,'ACTIVE','2026-08-05','2026-08-03','Lithuania','A','B',1,'EUR','OPENED');
\echo '--- T12 grants: installation scope uniqueness with NULL (expect second insert 23505)'
INSERT INTO claimcore.actors VALUES ('a0000000-0000-4000-8000-000000000001','HUMAN','https://issuer.example/','subject-1',true);
INSERT INTO claimcore.grants VALUES ('a0000000-0000-4000-8000-000000000001','OWNER',NULL);
INSERT INTO claimcore.grants VALUES ('a0000000-0000-4000-8000-000000000001','OWNER',NULL);
