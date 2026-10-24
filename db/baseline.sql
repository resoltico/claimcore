-- ClaimCore installation baseline.
--
-- Applied by `ClaimCore.Database initialize` as the schema owner, inside one transaction, to a
-- database that has no `claimcore` schema. The administration tool inserts the `installation`
-- row and the `baseline_identity` row for this source in that same transaction.
--
-- The role `claimcore_runtime` must already exist (cluster roles are an operator concern, not a
-- schema concern). It is the only role the HTTPS service connects as.
--
-- Integer domains: sequences and revisions are 1 .. 2^53-1 so every value is an exact JSON
-- number in every client (I-JSON, RFC 7493). Checks below encode format invariants only:
-- shape, typed ranges, permanent byte bounds and state invariants. Admission policy (character
-- counts, whitespace, invisible or directional characters, dates not in the future) belongs to
-- Domain and is applied to newly asserted values only; it is deliberately absent here so that a
-- later policy change never invalidates accepted rows.
--
-- Permanent byte bounds (RecordFormat FormatLimits): free business text is at most 1024 UTF-8
-- bytes; a ledger payload (framed request plus snapshot) is at most 32768 bytes.

CREATE SCHEMA claimcore;
REVOKE ALL ON SCHEMA claimcore FROM PUBLIC;

-- PostgreSQL already guarantees well-formed UTF-8 and excludes U+0000. I-JSON also excludes
-- noncharacters; this permanent representability check deliberately permits admission-policy
-- violations such as length, whitespace or directional formatting in retained business text.
CREATE FUNCTION claimcore.is_interchange_text(value text)
RETURNS boolean LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
SET search_path = pg_catalog
AS $$ SELECT value COLLATE "C" !~ U&'[\FDD0-\FDEF\FFFE\FFFF\+01FFFE\+01FFFF\+02FFFE\+02FFFF\+03FFFE\+03FFFF\+04FFFE\+04FFFF\+05FFFE\+05FFFF\+06FFFE\+06FFFF\+07FFFE\+07FFFF\+08FFFE\+08FFFF\+09FFFE\+09FFFF\+0AFFFE\+0AFFFF\+0BFFFE\+0BFFFF\+0CFFFE\+0CFFFF\+0DFFFE\+0DFFFF\+0EFFFE\+0EFFFF\+0FFFFE\+0FFFFF\+10FFFE\+10FFFF]' $$;

CREATE TABLE claimcore.baseline_identity (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    identifier text NOT NULL CHECK (identifier ~ '^[a-z][a-z0-9-]{0,63}$'),
    script_sha256 bytea NOT NULL CHECK (octet_length(script_sha256) = 32),
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE claimcore.installation (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    installation_id uuid NOT NULL
        CHECK (installation_id <> '00000000-0000-0000-0000-000000000000'),
    -- Immutable canonical IANA identifier, validated by the administration tool.
    business_time_zone text NOT NULL CHECK (
        char_length(business_time_zone) BETWEEN 1 AND 128
        AND business_time_zone = btrim(business_time_zone)
        AND business_time_zone !~ '[[:cntrl:]]'
    ),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- ---------------------------------------------------------------------------------------------
-- The ledger: the single record of everything ClaimCore has accepted.
--
-- Every accepted case act and every accepted authority act is exactly one row. Rows form one
-- gapless, hash-linked sequence. `entry_hash` is SHA-256 over the fixed 183-byte metadata
-- preimage defined by RecordFormat (entry format 2). The preimage covers `payload_commitment`,
-- never `payload`, so erasing a case's payload leaves the chain verifiable.
--
-- `subject_id` is the case for CASE rows and the target actor for AUTHORITY rows.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE claimcore.ledger (
    sequence bigint PRIMARY KEY CHECK (sequence BETWEEN 1 AND 9007199254740991),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    entry_hash bytea NOT NULL UNIQUE CHECK (octet_length(entry_hash) = 32),
    entry_format smallint NOT NULL CHECK (entry_format = 2),
    scope text NOT NULL CHECK (scope IN ('CASE', 'AUTHORITY')),
    act text NOT NULL,
    -- Caller-chosen identity of the accepted operation. Uniqueness is the idempotency guarantee.
    operation_id uuid NOT NULL UNIQUE
        CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    actor_id uuid NOT NULL CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
    recorded_at timestamptz NOT NULL CHECK (
        recorded_at BETWEEN TIMESTAMPTZ '0001-01-01T00:00:00Z'
                        AND TIMESTAMPTZ '9999-12-31T23:59:59.999999Z'
    ),
    subject_id uuid NOT NULL CHECK (subject_id <> '00000000-0000-0000-0000-000000000000'),
    case_revision bigint CHECK (case_revision BETWEEN 1 AND 9007199254740991),
    business_date date CHECK (business_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),

    -- SHA-256(payload_nonce || payload). Retained forever; reveals nothing once the nonce is gone.
    payload_commitment bytea NOT NULL CHECK (octet_length(payload_commitment) = 32),
    -- Length-framed canonical request and resulting snapshot (RecordFormat). Both columns become
    -- NULL together when, and only when, the case is erased. Authority payloads are never erased.
    payload bytea,
    payload_nonce bytea,

    CONSTRAINT ledger_scope_shape CHECK (
        (scope = 'CASE'
            AND case_revision IS NOT NULL
            AND business_date IS NOT NULL
            AND act IN ('OPEN', 'AMEND_REGISTRATION', 'CORRECT_CASE', 'DECIDE',
                        'WITHDRAW_DECISION', 'RECORD_PAYMENT', 'CLEAR_PAYMENT', 'CLOSE',
                        'REOPEN', 'VOID', 'REINSTATE', 'ERASE'))
        OR (scope = 'AUTHORITY'
            AND case_revision IS NULL
            AND business_date IS NULL
            AND payload IS NOT NULL
            AND act IN ('PROVISION_OWNER', 'REGISTER_ACTOR', 'SET_GRANT', 'SET_ENABLED'))
    ),
    CONSTRAINT ledger_payload_shape CHECK (
        (payload IS NULL AND payload_nonce IS NULL)
        OR (payload IS NOT NULL AND payload_nonce IS NOT NULL
            AND octet_length(payload) BETWEEN 9 AND 32768
            AND octet_length(payload_nonce) = 32)
    ),
    -- Authority rows have NULL revision and therefore never collide here.
    CONSTRAINT ledger_case_revision_unique UNIQUE (subject_id, case_revision)
);

CREATE TABLE claimcore.ledger_head (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    sequence bigint NOT NULL CHECK (sequence BETWEEN 0 AND 9007199254740991),
    entry_hash bytea NOT NULL CHECK (octet_length(entry_hash) = 32)
);

INSERT INTO claimcore.ledger_head (sequence, entry_hash)
VALUES (0, decode(repeat('00', 32), 'hex'));

-- Serialises writers and returns the head they must extend, with the one instant the write is
-- recorded and dated at. The runtime calls this first in every READ COMMITTED write transaction.
-- The clock is read in its own statement, after the row lock is held: a locking SELECT's target
-- list is evaluated before its lock wait, which would date a waiting writer too early.
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

-- Structural integrity: a row is admitted only as the next link after the current head.
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

-- Append-only, enforced for every role including the owner: the only permitted change to an
-- existing row clears the payload and nonce of a CASE row. Deletion and truncation are refused.
-- Whether that clearing was authorised by an accepted ERASE is checked by erase_case_payload
-- and by verification.
CREATE FUNCTION claimcore.ledger_guard()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'ledger rows are never removed' USING ERRCODE = 'CC003';
    END IF;

    IF OLD.scope <> 'CASE'
       OR NEW.payload IS NOT NULL
       OR NEW.payload_nonce IS NOT NULL
       OR (NEW.sequence, NEW.previous_hash, NEW.entry_hash, NEW.entry_format, NEW.scope,
           NEW.act, NEW.operation_id, NEW.actor_id, NEW.recorded_at, NEW.subject_id,
           NEW.case_revision, NEW.business_date, NEW.payload_commitment)
          IS DISTINCT FROM
          (OLD.sequence, OLD.previous_hash, OLD.entry_hash, OLD.entry_format, OLD.scope,
           OLD.act, OLD.operation_id, OLD.actor_id, OLD.recorded_at, OLD.subject_id,
           OLD.case_revision, OLD.business_date, OLD.payload_commitment)
    THEN
        RAISE EXCEPTION 'ledger rows change only by case erasure' USING ERRCODE = 'CC003';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER ledger_guard_rows
    BEFORE UPDATE OR DELETE ON claimcore.ledger
    FOR EACH ROW EXECUTE FUNCTION claimcore.ledger_guard();

CREATE TRIGGER ledger_guard_truncate
    BEFORE TRUNCATE ON claimcore.ledger
    FOR EACH STATEMENT EXECUTE FUNCTION claimcore.ledger_guard();

-- The runtime-authorized path by which ledger bytes are removed. It refuses unless an accepted ERASE
-- entry for the case already exists, and it touches nothing but that case's erasable columns.
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
         WHERE scope = 'CASE' AND subject_id = target_case_id AND act = 'ERASE'
    ) THEN
        RAISE EXCEPTION 'case has no accepted ERASE entry'
            USING ERRCODE = 'CC002';
    END IF;

    UPDATE claimcore.ledger
       SET payload = NULL, payload_nonce = NULL
     WHERE scope = 'CASE' AND subject_id = target_case_id AND payload IS NOT NULL;

    GET DIAGNOSTICS erased = ROW_COUNT;
    RETURN erased;
END;
$$;

-- ---------------------------------------------------------------------------------------------
-- Projections. Derived from the ledger in the same transaction that appends to it; `verify`
-- recomputes them from the ledger and reports any divergence. They are never an authority.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE claimcore.cases (
    -- SHA-256-derived from the installation and the ASCII-uppercased reference (RecordFormat).
    case_id uuid PRIMARY KEY CHECK (case_id <> '00000000-0000-0000-0000-000000000000'),
    -- Authored spelling; immutable, never reused, retained after erasure.
    case_reference text COLLATE "C" NOT NULL,
    revision bigint NOT NULL CHECK (revision BETWEEN 1 AND 9007199254740991),
    disposition text NOT NULL CHECK (disposition IN ('ACTIVE', 'VOIDED', 'ERASED')),

    -- Business payload columns follow Contract FieldTable; case_reference above survives erasure.
    incident_date date CHECK (incident_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    incident_notification_date date CHECK (incident_notification_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    incident_country text,
    claimant_name text,
    insurer_name text,
    claimed_amount numeric,
    claimed_currency text,
    payment_decision_date date CHECK (payment_decision_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    payable_amount numeric,
    payable_currency text,
    payment_date date CHECK (payment_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    status text CHECK (status IN ('OPENED', 'CLOSED')),

    -- Printable ASCII without space: PRECIS IdentifierClass's ASCII subset (RFC 8264 4.2).
    CONSTRAINT reference_shape CHECK (case_reference ~ '^[!-~]{1,80}$'),
    CONSTRAINT erased_shape CHECK (
        (disposition = 'ERASED' AND num_nonnulls(
            incident_date, incident_notification_date, incident_country, claimant_name,
            insurer_name, claimed_amount, claimed_currency, payment_decision_date,
            payable_amount, payable_currency, payment_date, status) = 0)
        OR (disposition <> 'ERASED' AND num_nulls(
            incident_date, incident_notification_date, incident_country, claimant_name,
            insurer_name, claimed_amount, claimed_currency, status) = 0)
    ),
    CONSTRAINT text_present CHECK (
        claimant_name <> '' AND insurer_name <> ''
        AND octet_length(claimant_name) <= 1024 AND octet_length(insurer_name) <= 1024
        AND claimcore.is_interchange_text(claimant_name)
        AND claimcore.is_interchange_text(insurer_name)
    ),
    -- ISO 3166-1 alpha-2 syntax; membership is not checked.
    CONSTRAINT country_shape CHECK (incident_country COLLATE "C" ~ '^[A-Z]{2}$'),
    CONSTRAINT claimed_money CHECK (
        claimed_amount >= 0 AND claimed_amount <= 999999999999999999.9999
        AND scale(claimed_amount) <= 4 AND claimed_currency COLLATE "C" ~ '^[A-Z]{3}$'
    ),
    CONSTRAINT decision_group CHECK (
        num_nonnulls(payment_decision_date, payable_amount, payable_currency) IN (0, 3)
    ),
    CONSTRAINT payable_money CHECK (
        payable_amount >= 0 AND payable_amount <= 999999999999999999.9999
        AND scale(payable_amount) <= 4 AND payable_currency COLLATE "C" ~ '^[A-Z]{3}$'
    ),
    CONSTRAINT chronology CHECK (
        incident_notification_date >= incident_date
        AND payment_decision_date >= incident_notification_date
        AND payment_date >= payment_decision_date
    ),
    CONSTRAINT paid_requires_positive_decision CHECK (
        payment_date IS NULL OR (payment_decision_date IS NOT NULL AND payable_amount > 0)
    )
);

-- One case per reference regardless of ASCII letter case; also the list order and keyset.
CREATE UNIQUE INDEX cases_reference_key ON claimcore.cases (upper(case_reference COLLATE "C"));

CREATE TABLE claimcore.actors (
    -- The operation ID of the authority entry that registered the actor.
    actor_id uuid PRIMARY KEY CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
    principal_kind text NOT NULL CHECK (principal_kind IN ('HUMAN', 'SERVICE')),
    issuer text NOT NULL CHECK (char_length(issuer) BETWEEN 1 AND 2048 AND claimcore.is_interchange_text(issuer)),
    subject text NOT NULL CHECK (char_length(subject) BETWEEN 1 AND 512 AND claimcore.is_interchange_text(subject)),
    enabled boolean NOT NULL,
    UNIQUE (issuer, principal_kind, subject)
);

CREATE TABLE claimcore.grants (
    actor_id uuid NOT NULL REFERENCES claimcore.actors (actor_id),
    -- Role tokens only; the role-to-capability matrix lives in one F# table and never in SQL.
    role text NOT NULL CHECK (role IN (
        'OWNER', 'CASE_READER', 'CASE_REGISTRAR', 'CASE_HANDLER', 'PAYMENT_RECORDER',
        'DATA_STEWARD', 'AUDITOR'
    )),
    -- NULL grants the role across the installation; otherwise only on this case.
    case_id uuid REFERENCES claimcore.cases (case_id),
    CONSTRAINT grants_unique UNIQUE NULLS NOT DISTINCT (actor_id, role, case_id)
);

CREATE INDEX grants_case ON claimcore.grants (case_id) WHERE case_id IS NOT NULL;

-- Resource-scoped authority reads load an actor's installation grants (case_id IS NULL) and its
-- grants on the one case a request targets, never the actor's whole grant set.
CREATE INDEX grants_actor_case ON claimcore.grants (actor_id, case_id);

-- ---------------------------------------------------------------------------------------------
-- Runtime privileges: least privilege for the one role the service connects as. It can append to
-- the ledger but cannot change or remove a row except through erase_case_payload; it can
-- maintain projections; it cannot run DDL.
-- ---------------------------------------------------------------------------------------------
REVOKE ALL ON ALL TABLES IN SCHEMA claimcore FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA claimcore FROM PUBLIC;

GRANT USAGE ON SCHEMA claimcore TO claimcore_runtime;
GRANT SELECT ON claimcore.baseline_identity, claimcore.installation, claimcore.ledger_head
    TO claimcore_runtime;
GRANT SELECT, INSERT ON claimcore.ledger TO claimcore_runtime;
GRANT SELECT, INSERT, UPDATE ON claimcore.cases, claimcore.actors TO claimcore_runtime;
GRANT SELECT, INSERT, DELETE ON claimcore.grants TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.lock_ledger_head() TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.erase_case_payload(uuid) TO claimcore_runtime;

GRANT EXECUTE ON FUNCTION claimcore.is_interchange_text(text) TO claimcore_runtime;
