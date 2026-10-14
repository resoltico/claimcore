-- ClaimCore schema migration 0001: baseline.
--
-- Applied by `ClaimCore.Database initialize` as the schema owner, inside one transaction, to a
-- database that has no `claimcore` schema. The administration tool inserts the `installation`
-- row and the `schema_migrations` row for this script in that same transaction.
--
-- The role `claimcore_runtime` must already exist (cluster roles are an operator concern, not a
-- migration concern). It is the only role the HTTPS service connects as.

CREATE SCHEMA claimcore;
REVOKE ALL ON SCHEMA claimcore FROM PUBLIC;

-- One row per applied migration script, in order. The runtime refuses to serve unless the highest
-- version equals the version it was compiled against.
CREATE TABLE claimcore.schema_migrations (
    version integer PRIMARY KEY CHECK (version > 0),
    name text NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,63}$'),
    script_sha256 bytea NOT NULL CHECK (octet_length(script_sha256) = 32),
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE claimcore.installation (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    installation_id uuid NOT NULL
        CHECK (installation_id <> '00000000-0000-0000-0000-000000000000'),
    -- Immutable canonical IANA identifier. Business dates derive from one captured instant and
    -- this zone; validated by the administration tool before it is stored.
    business_time_zone text NOT NULL CHECK (
        char_length(business_time_zone) BETWEEN 1 AND 128
        AND business_time_zone = btrim(business_time_zone)
        AND business_time_zone !~ '[[:cntrl:]]'
    ),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- ---------------------------------------------------------------------------------------------
-- The ledger: the single authority for everything ClaimCore has accepted.
--
-- Every accepted case command and every accepted authority change is exactly one row. Rows form
-- one gapless, hash-linked sequence. `entry_hash` is computed by ClaimCore.RecordFormat over the
-- row's fixed metadata and `payload_commitment`; it never covers `payload` directly, so erasing a
-- case's payload leaves the chain verifiable.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE claimcore.ledger (
    sequence bigint PRIMARY KEY CHECK (sequence > 0),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    entry_hash bytea NOT NULL UNIQUE CHECK (octet_length(entry_hash) = 32),
    entry_format smallint NOT NULL CHECK (entry_format = 1),
    kind text NOT NULL CHECK (kind IN ('CASE', 'AUTHORITY')),
    -- Caller-chosen identity of the accepted operation. Uniqueness is the idempotency guarantee.
    operation_id uuid NOT NULL UNIQUE
        CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    actor_id uuid NOT NULL,
    recorded_at timestamptz NOT NULL,

    -- Present exactly for CASE entries.
    case_id uuid,
    case_revision bigint CHECK (case_revision > 0 AND case_revision < 9223372036854775807),
    command_kind text CHECK (command_kind IN (
        'OPEN', 'AMEND_REGISTRATION', 'CORRECT_CASE', 'DECIDE', 'WITHDRAW_DECISION',
        'RECORD_PAYMENT', 'CLEAR_PAYMENT', 'CLOSE', 'REOPEN',
        'VOID', 'REINSTATE', 'ERASE'
    )),
    business_date date,
    rule_revision smallint CHECK (rule_revision > 0),

    -- SHA-256(payload_nonce || payload). Retained forever; reveals nothing once the nonce is gone.
    payload_commitment bytea NOT NULL CHECK (octet_length(payload_commitment) = 32),
    -- Canonical bytes (RecordFormat): the exact accepted request and the resulting snapshot.
    -- All three are set to NULL together when the case is erased, and only then.
    payload bytea,
    payload_nonce bytea,
    request_sha256 bytea,

    CONSTRAINT ledger_kind_shape CHECK (
        (kind = 'CASE' AND num_nulls(case_id, case_revision, command_kind, business_date, rule_revision) = 0)
        OR (kind = 'AUTHORITY' AND num_nonnulls(case_id, case_revision, command_kind, business_date, rule_revision) = 0)
    ),
    CONSTRAINT ledger_payload_shape CHECK (
        num_nonnulls(payload, payload_nonce, request_sha256) = 0
        OR (num_nulls(payload, payload_nonce, request_sha256) = 0
            AND octet_length(payload) BETWEEN 1 AND 262144
            AND octet_length(payload_nonce) = 32
            AND octet_length(request_sha256) = 32)
    ),
    CONSTRAINT ledger_case_revision_unique UNIQUE (case_id, case_revision)
);

CREATE TABLE claimcore.ledger_head (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    sequence bigint NOT NULL CHECK (sequence >= 0),
    entry_hash bytea NOT NULL CHECK (octet_length(entry_hash) = 32)
);

INSERT INTO claimcore.ledger_head (sequence, entry_hash)
VALUES (0, decode(repeat('00', 32), 'hex'));

-- Serialises writers and returns the head they must extend, with the one instant the write is
-- recorded and dated at. The runtime calls this first in every READ COMMITTED write transaction,
-- so authorization, idempotency and the Domain decision are all evaluated against state no
-- concurrent writer can change before COMMIT. `observed_at` is read after the lock is held: time
-- spent waiting for another writer can never date a decision earlier than its predecessor.
--
-- The clock is read in its own statement. Reading it in the locking SELECT's target list evaluates
-- it before the row-lock wait (verified on PostgreSQL 18.6), which would date a waiting writer
-- before the writer it waited for.
CREATE FUNCTION claimcore.lock_ledger_head(
    OUT sequence bigint,
    OUT entry_hash bytea,
    OUT observed_at timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
BEGIN
    SELECT h.sequence, h.entry_hash
      INTO STRICT sequence, entry_hash
      FROM claimcore.ledger_head AS h
     WHERE h.singleton
       FOR UPDATE;

    observed_at := clock_timestamp();
END;
$$;

-- Structural integrity only: a row is admitted only as the next link after the current head.
CREATE FUNCTION claimcore.ledger_extend_head()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
DECLARE
    head claimcore.ledger_head%ROWTYPE;
BEGIN
    SELECT * INTO STRICT head FROM claimcore.ledger_head WHERE singleton FOR UPDATE;

    IF NEW.sequence <> head.sequence + 1 OR NEW.previous_hash <> head.entry_hash THEN
        RAISE EXCEPTION 'ledger entry does not extend the current head'
            USING ERRCODE = 'CC001';
    END IF;

    UPDATE claimcore.ledger_head
       SET sequence = NEW.sequence, entry_hash = NEW.entry_hash
     WHERE singleton;

    RETURN NEW;
END;
$$;

CREATE TRIGGER ledger_extend_head
    BEFORE INSERT ON claimcore.ledger
    FOR EACH ROW EXECUTE FUNCTION claimcore.ledger_extend_head();

-- The only path by which ledger bytes are ever removed. It refuses unless an accepted ERASE entry
-- for the case already exists, and it touches nothing but that case's erasable columns.
CREATE FUNCTION claimcore.erase_case_payload(target_case_id uuid)
RETURNS bigint
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
DECLARE
    erased bigint;
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM claimcore.ledger
         WHERE case_id = target_case_id AND command_kind = 'ERASE'
    ) THEN
        RAISE EXCEPTION 'case has no accepted ERASE entry'
            USING ERRCODE = 'CC002';
    END IF;

    UPDATE claimcore.ledger
       SET payload = NULL, payload_nonce = NULL, request_sha256 = NULL
     WHERE case_id = target_case_id AND payload IS NOT NULL;

    GET DIAGNOSTICS erased = ROW_COUNT;
    RETURN erased;
END;
$$;

-- ---------------------------------------------------------------------------------------------
-- Projections. Derived from the ledger in the same transaction that appends to it; `verify`
-- recomputes them from the ledger and refuses divergence. They are never an authority.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE claimcore.cases (
    case_id uuid PRIMARY KEY CHECK (case_id <> '00000000-0000-0000-0000-000000000000'),
    -- Immutable, case-sensitive, never reused, retained after erasure.
    case_reference text COLLATE "C" NOT NULL UNIQUE,
    revision bigint NOT NULL CHECK (revision > 0 AND revision < 9223372036854775807),
    disposition text NOT NULL CHECK (disposition IN ('ACTIVE', 'VOIDED', 'ERASED')),

    -- The thirteen business fields (case_reference above is the eighth). All NULL once erased.
    incident_date date,
    incident_notification_date date,
    incident_country text,
    claimant_name text,
    insurer_name text,
    claimed_amount numeric,
    claimed_currency text,
    payment_decision_date date,
    payable_amount numeric,
    payable_currency text,
    payment_date date,
    status text CHECK (status IN ('OPENED', 'CLOSED')),

    CONSTRAINT erased_shape CHECK (
        (disposition = 'ERASED' AND num_nonnulls(
            incident_date, incident_notification_date, incident_country, claimant_name,
            insurer_name, claimed_amount, claimed_currency, payment_decision_date,
            payable_amount, payable_currency, payment_date, status) = 0)
        OR (disposition <> 'ERASED' AND num_nulls(
            incident_date, incident_notification_date, incident_country, claimant_name,
            insurer_name, claimed_amount, claimed_currency, status) = 0)
    ),
    CONSTRAINT reference_shape CHECK (
        char_length(case_reference) BETWEEN 1 AND 80
        AND btrim(case_reference) = case_reference
        AND case_reference !~ '[[:cntrl:]]'
    ),
    CONSTRAINT name_shape CHECK (
        char_length(incident_country) BETWEEN 1 AND 100
        AND char_length(claimant_name) BETWEEN 1 AND 200
        AND char_length(insurer_name) BETWEEN 1 AND 200
        AND incident_country = btrim(incident_country)
        AND claimant_name = btrim(claimant_name)
        AND insurer_name = btrim(insurer_name)
        AND incident_country !~ '[[:cntrl:]]'
        AND claimant_name !~ '[[:cntrl:]]'
        AND insurer_name !~ '[[:cntrl:]]'
    ),
    CONSTRAINT claimed_money CHECK (
        claimed_amount >= 0 AND claimed_amount <= 999999999999999999.9999
        AND scale(claimed_amount) <= 4 AND claimed_currency ~ '^[A-Z]{3}$'
    ),
    CONSTRAINT decision_group CHECK (
        num_nonnulls(payment_decision_date, payable_amount, payable_currency) IN (0, 3)
    ),
    CONSTRAINT payable_money CHECK (
        payable_amount >= 0 AND payable_amount <= 999999999999999999.9999
        AND scale(payable_amount) <= 4 AND payable_currency ~ '^[A-Z]{3}$'
    ),
    CONSTRAINT chronology CHECK (
        incident_date >= DATE '0001-01-01'
        AND incident_notification_date >= incident_date
        AND payment_decision_date >= incident_notification_date
        AND payment_date >= payment_decision_date
    ),
    CONSTRAINT paid_requires_positive_decision CHECK (
        payment_date IS NULL OR (payment_decision_date IS NOT NULL AND payable_amount > 0)
    )
);

CREATE TABLE claimcore.actors (
    actor_id uuid PRIMARY KEY CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
    principal_kind text NOT NULL CHECK (principal_kind IN ('HUMAN', 'SERVICE')),
    issuer text NOT NULL CHECK (char_length(issuer) BETWEEN 1 AND 2048),
    subject text NOT NULL CHECK (char_length(subject) BETWEEN 1 AND 512),
    enabled boolean NOT NULL,
    UNIQUE (issuer, principal_kind, subject)
);

CREATE TABLE claimcore.grants (
    actor_id uuid NOT NULL REFERENCES claimcore.actors (actor_id),
    role text NOT NULL CHECK (role IN (
        'OWNER', 'CASE_READER', 'CASE_EDITOR', 'DATA_STEWARD', 'AUDITOR'
    )),
    -- NULL grants the role across the installation; otherwise only on this case.
    case_id uuid REFERENCES claimcore.cases (case_id),
    CONSTRAINT grants_unique UNIQUE NULLS NOT DISTINCT (actor_id, role, case_id)
);

CREATE INDEX grants_case ON claimcore.grants (case_id) WHERE case_id IS NOT NULL;
CREATE INDEX ledger_case ON claimcore.ledger (case_id, case_revision) WHERE case_id IS NOT NULL;

-- ---------------------------------------------------------------------------------------------
-- Runtime privileges: least privilege for the one role the service connects as. It can append to
-- the ledger but cannot change or remove a row; it can maintain projections; it cannot run DDL.
-- ---------------------------------------------------------------------------------------------
REVOKE ALL ON ALL TABLES IN SCHEMA claimcore FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA claimcore FROM PUBLIC;

GRANT USAGE ON SCHEMA claimcore TO claimcore_runtime;
GRANT SELECT ON claimcore.schema_migrations, claimcore.installation, claimcore.ledger_head
    TO claimcore_runtime;
GRANT SELECT, INSERT ON claimcore.ledger TO claimcore_runtime;
GRANT SELECT, INSERT, UPDATE ON claimcore.cases, claimcore.actors TO claimcore_runtime;
GRANT SELECT, INSERT, DELETE ON claimcore.grants TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.lock_ledger_head() TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.erase_case_payload(uuid) TO claimcore_runtime;
