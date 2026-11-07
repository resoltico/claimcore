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
-- Permanent scalar grammars and business-text byte bounds: src/ClaimCore.Domain/Values.fs.
-- Durable payload grammar and byte bounds: src/ClaimCore.RecordFormat/LedgerPayload.fs.
-- Narrative syntax is at most 4096 UTF-8 bytes; reminder action syntax is at most 1024 bytes.
-- Each framed request plus affected-entity snapshot payload is at most 32768 bytes.

-- Server module supplied with PostgreSQL; used only by indexes for existing literal LIKE searches.
CREATE EXTENSION pg_trgm WITH SCHEMA public VERSION '1.6';

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

-- Operational mode is owner-controlled and survives process restarts. It is not a ledger projection.
CREATE TABLE claimcore.register_maintenance (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    active boolean NOT NULL,
    generation bigint NOT NULL CHECK (generation BETWEEN 0 AND 9007199254740991),
    CHECK (NOT active OR generation > 0)
);
INSERT INTO claimcore.register_maintenance(singleton,active,generation) VALUES (true,false,0);

-- ---------------------------------------------------------------------------------------------
-- The ledger: the single record of everything ClaimCore has accepted.
--
-- Accepted operations have one root and bounded, consecutive effect rows. Rows form one
-- gapless, hash-linked sequence. `entry_hash` is SHA-256 over the length-framed metadata
-- preimage defined by RecordFormat/LedgerEntry.fs (entry format 5). The preimage covers `payload_commitment`,
-- never `payload`, so erasing a case's payload leaves the chain verifiable.
--
-- `subject_id` is the case for CASE rows and the target actor for AUTHORITY rows.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE claimcore.ledger (
    sequence bigint PRIMARY KEY CHECK (sequence BETWEEN 1 AND 9007199254740991),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    entry_hash bytea NOT NULL UNIQUE CHECK (octet_length(entry_hash) = 32),
    entry_format smallint NOT NULL CHECK (entry_format = 6),
    subject_kind text NOT NULL CHECK (subject_kind IN ('CASE', 'AUTHORITY', 'WORK', 'REMINDER')),
    act text NOT NULL,
    -- Caller-chosen identity of the accepted operation. Uniqueness is the idempotency guarantee.
    operation_id uuid NOT NULL
        CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    effect_index integer NOT NULL CHECK (effect_index BETWEEN 0 AND 49),
    effect_count integer NOT NULL CHECK (effect_count BETWEEN 1 AND 50 AND effect_index < effect_count),
    authority_operation text,
    CONSTRAINT ledger_operation_shape CHECK (
        (subject_kind = 'AUTHORITY' AND authority_operation IS NOT NULL AND authority_operation ~ '^[A-Z][A-Z_]{0,39}$'
         AND ((authority_operation IN ('REGISTER','PROVISION_OWNER') AND effect_count=2
               AND ((effect_index=0 AND act=authority_operation) OR (effect_index=1 AND act='SET_PROFILE')))
              OR (authority_operation='ADMIT' AND effect_count BETWEEN 3 AND 50 AND ((effect_index=0 AND act='REGISTER') OR (effect_index=1 AND act='SET_PROFILE') OR (effect_index>=2 AND act='GRANT')))
              OR (authority_operation NOT IN ('REGISTER','PROVISION_OWNER','ADMIT') AND effect_count=1 AND effect_index=0 AND act=authority_operation)))
        OR (subject_kind <> 'AUTHORITY' AND effect_index=0 AND effect_count=1 AND authority_operation IS NULL)
    ),
    actor_id uuid NOT NULL CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
    recorded_at timestamptz NOT NULL CHECK (
        recorded_at BETWEEN TIMESTAMPTZ '0001-01-01T00:00:00Z'
                        AND TIMESTAMPTZ '9999-12-31T23:59:59.999999Z'
    ),
    subject_id uuid NOT NULL CHECK (subject_id <> '00000000-0000-0000-0000-000000000000'),
    case_revision bigint CHECK (case_revision BETWEEN 1 AND 9007199254740991),
    parent_case_id uuid CHECK (parent_case_id <> '00000000-0000-0000-0000-000000000000'),
    next_reminder_id uuid CHECK (next_reminder_id <> '00000000-0000-0000-0000-000000000000'),
    business_date date CHECK (business_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),

    -- SHA-256(payload_nonce || payload). Retained forever; reveals nothing once the nonce is gone.
    payload_commitment bytea NOT NULL CHECK (octet_length(payload_commitment) = 32),
    -- Length-framed canonical request and resulting snapshot (RecordFormat). Both columns become
    -- NULL together after case erasure, or for SET_PROFILE effects after a later profile erasure.
    -- Identity and grant payloads are retained; profile erasure never removes them.
    payload bytea,
    payload_nonce bytea,
    creation_reference text,
    CONSTRAINT ledger_creation_shape CHECK (
        (subject_kind = 'CASE' AND act = 'OPEN' AND case_revision = 1
         AND subject_id = operation_id AND creation_reference IS NOT NULL
         AND creation_reference COLLATE "C" ~ '^[!-~]{1,80}$')
        OR ((subject_kind <> 'CASE' OR act <> 'OPEN') AND creation_reference IS NULL)
    ),

    CONSTRAINT ledger_subject_shape CHECK (
        (subject_kind = 'CASE'
            AND parent_case_id IS NULL AND next_reminder_id IS NULL
            AND case_revision IS NOT NULL
            AND business_date IS NOT NULL
            AND act ~ '^[A-Z][A-Z_]{0,39}$')
        OR (subject_kind = 'WORK' AND act = 'UPDATE_WORK' AND case_revision IS NOT NULL AND business_date IS NOT NULL AND parent_case_id = subject_id AND next_reminder_id IS NULL)
        OR (subject_kind = 'REMINDER' AND case_revision IS NOT NULL AND business_date IS NOT NULL AND parent_case_id IS NOT NULL AND act IN ('CREATE','UPDATE','COMPLETE','CANCEL','REOPEN','COMPLETE_AND_PLAN_NEXT') AND ((act='CREATE')=(case_revision=1)) AND (act<>'CREATE' OR subject_id=operation_id) AND ((act='COMPLETE_AND_PLAN_NEXT' AND next_reminder_id=operation_id AND next_reminder_id<>subject_id) OR (act<>'COMPLETE_AND_PLAN_NEXT' AND next_reminder_id IS NULL)))
        OR (subject_kind = 'AUTHORITY'
            AND parent_case_id IS NULL AND next_reminder_id IS NULL
            AND case_revision IS NULL
            AND business_date IS NULL
            AND (payload IS NOT NULL OR act='SET_PROFILE')
            AND act ~ '^[A-Z][A-Z_]{0,39}$')
    ),
    CONSTRAINT ledger_payload_shape CHECK (
        (payload IS NULL AND payload_nonce IS NULL)
        OR (payload IS NOT NULL AND payload_nonce IS NOT NULL
            AND octet_length(payload) BETWEEN 9 AND 32768
            AND octet_length(payload_nonce) = 32)
    ),
    -- Authority rows have NULL revision and therefore never collide here.
    CONSTRAINT ledger_case_revision_unique UNIQUE (subject_kind, subject_id, case_revision)
);

-- Retained authoritative references cannot be reused even if a projection is damaged.
CREATE UNIQUE INDEX ledger_accepted_operation ON claimcore.ledger (operation_id,effect_index);
-- Commit-time completeness keeps partial roots and mixed groups from becoming accepted operations.
CREATE FUNCTION claimcore.complete_ledger_operation()
RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
    root claimcore.ledger%ROWTYPE;
    actual_count bigint;
    coherent boolean;
    nonce_count bigint;
    live_nonce_count bigint;
BEGIN
    IF NEW.subject_kind='AUTHORITY' AND NEW.act='SET_PROFILE'
       AND (SELECT payload IS NULL FROM claimcore.ledger WHERE sequence=NEW.sequence)
       AND NOT EXISTS(SELECT 1 FROM claimcore.ledger erased WHERE erased.subject_kind='AUTHORITY'
                      AND erased.act='ERASE_PROFILE' AND erased.subject_id=NEW.subject_id AND erased.sequence>NEW.sequence) THEN
        RAISE EXCEPTION 'profile payload has no later erasure' USING ERRCODE='CC004';
    END IF;
    IF NEW.effect_count = 1 THEN RETURN NULL; END IF;
    -- Deferred INSERT events retain the original nonce even if this transaction later erases its profile.
    IF NEW.payload_nonce IS NOT NULL AND EXISTS(
        SELECT 1 FROM claimcore.ledger sibling WHERE sibling.operation_id=NEW.operation_id
          AND sibling.sequence<>NEW.sequence
          AND sibling.payload_nonce=NEW.payload_nonce
    ) THEN
        RAISE EXCEPTION 'operation effects reuse a payload nonce' USING ERRCODE='CC004';
    END IF;

    SELECT * INTO root FROM claimcore.ledger
      WHERE operation_id = NEW.operation_id AND effect_index = 0;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'operation effect has no root' USING ERRCODE = 'CC004';
    END IF;
    IF NEW.effect_index <> 0 THEN
        IF NEW.effect_count IS DISTINCT FROM root.effect_count OR NEW.actor_id IS DISTINCT FROM root.actor_id
           OR NEW.recorded_at IS DISTINCT FROM root.recorded_at OR NEW.subject_id IS DISTINCT FROM root.subject_id
           OR NEW.authority_operation IS DISTINCT FROM root.authority_operation
           OR NEW.sequence <> root.sequence + NEW.effect_index THEN
            RAISE EXCEPTION 'operation effect differs from its root' USING ERRCODE = 'CC004';
        END IF;
        RETURN NULL;
    END IF;
    SELECT count(*), bool_and(effect_count=root.effect_count
          AND actor_id=root.actor_id AND recorded_at=root.recorded_at
          AND sequence=root.sequence+effect_index
          AND (root.effect_count=1 OR (subject_kind='AUTHORITY' AND subject_id=root.subject_id
              AND authority_operation=root.authority_operation))), count(DISTINCT payload_nonce), count(payload_nonce)
      INTO actual_count, coherent, nonce_count, live_nonce_count
      FROM claimcore.ledger WHERE operation_id=NEW.operation_id;
    IF actual_count <> root.effect_count OR coherent IS NOT TRUE
       OR (nonce_count <> live_nonce_count) THEN
        RAISE EXCEPTION 'operation effects are incomplete or inconsistent' USING ERRCODE = 'CC004';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER ledger_operation_complete
    AFTER INSERT ON claimcore.ledger DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION claimcore.complete_ledger_operation();


CREATE UNIQUE INDEX ledger_creation_reference ON claimcore.ledger (upper(creation_reference COLLATE "C")) WHERE creation_reference IS NOT NULL;

CREATE INDEX ledger_authority_sequence ON claimcore.ledger (sequence DESC)
    WHERE subject_kind = 'AUTHORITY';
CREATE INDEX ledger_authority_actor ON claimcore.ledger (subject_id, sequence DESC)
    WHERE subject_kind = 'AUTHORITY';

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

    IF (OLD.subject_kind NOT IN ('CASE','WORK','REMINDER')
        AND NOT (OLD.subject_kind='AUTHORITY' AND OLD.act='SET_PROFILE'
                 AND EXISTS(SELECT 1 FROM claimcore.ledger erased WHERE erased.subject_kind='AUTHORITY'
                            AND erased.act='ERASE_PROFILE' AND erased.subject_id=OLD.subject_id AND erased.sequence>OLD.sequence)))
       OR NEW.payload IS NOT NULL
       OR NEW.payload_nonce IS NOT NULL
       OR (NEW.sequence, NEW.previous_hash, NEW.entry_hash, NEW.entry_format, NEW.subject_kind,
           NEW.act, NEW.operation_id, NEW.effect_index, NEW.effect_count, NEW.authority_operation, NEW.actor_id, NEW.recorded_at, NEW.subject_id,
           NEW.case_revision, NEW.business_date, NEW.creation_reference, NEW.parent_case_id, NEW.next_reminder_id, NEW.payload_commitment)
          IS DISTINCT FROM
          (OLD.sequence, OLD.previous_hash, OLD.entry_hash, OLD.entry_format, OLD.subject_kind,
           OLD.act, OLD.operation_id, OLD.effect_index, OLD.effect_count, OLD.authority_operation, OLD.actor_id, OLD.recorded_at, OLD.subject_id,
           OLD.case_revision, OLD.business_date, OLD.creation_reference, OLD.parent_case_id, OLD.next_reminder_id, OLD.payload_commitment)
    THEN
        RAISE EXCEPTION 'ledger rows change only by authorized payload erasure' USING ERRCODE = 'CC003';
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
         WHERE subject_kind = 'CASE' AND subject_id = target_case_id AND act = 'ERASE'
    ) THEN
        RAISE EXCEPTION 'case has no accepted ERASE entry'
            USING ERRCODE = 'CC002';
    END IF;

    UPDATE claimcore.ledger
       SET payload = NULL, payload_nonce = NULL
     WHERE ((subject_kind = 'CASE' AND subject_id = target_case_id) OR (subject_kind IN ('WORK','REMINDER') AND parent_case_id = target_case_id)) AND payload IS NOT NULL;

    GET DIAGNOSTICS erased = ROW_COUNT;
    UPDATE claimcore.case_work SET snapshot=NULL,responsible_actor=NULL,handling_summary=NULL,search_text=NULL,summary_changed_at=NULL,summary_recorded_by=NULL WHERE case_id=target_case_id;
    UPDATE claimcore.reminders SET snapshot=NULL,state=NULL,action=NULL,due=NULL,search_text=NULL WHERE case_id=target_case_id;
    RETURN erased;
END;
$$;

-- A retained erase frontier authorizes only earlier SET_PROFILE bytes, never a later re-profile.
CREATE FUNCTION claimcore.erase_profile_payload(target_actor_id uuid)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE frontier bigint; erased bigint;
BEGIN
    SELECT max(sequence) INTO frontier FROM claimcore.ledger
     WHERE subject_kind='AUTHORITY' AND act='ERASE_PROFILE' AND subject_id=target_actor_id;
    IF frontier IS NULL THEN
        RAISE EXCEPTION 'actor has no accepted ERASE_PROFILE entry' USING ERRCODE='CC002';
    END IF;
    UPDATE claimcore.ledger SET payload=NULL,payload_nonce=NULL
     WHERE subject_kind='AUTHORITY' AND act='SET_PROFILE' AND subject_id=target_actor_id
       AND sequence<frontier AND payload IS NOT NULL;
    GET DIAGNOSTICS erased=ROW_COUNT;
    UPDATE claimcore.actors SET display_name=NULL,contact=NULL,display_name_search=NULL,contact_search=NULL WHERE actor_id=target_actor_id
       AND NOT EXISTS(SELECT 1 FROM claimcore.ledger later WHERE later.subject_kind='AUTHORITY'
                      AND later.act='SET_PROFILE' AND later.subject_id=target_actor_id AND later.sequence>frontier);
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
    state text NOT NULL CHECK (state IN ('ACTIVE', 'VOIDED', 'ERASED')),

    snapshot bytea,
    status text CHECK (status ~ '^[A-Z][A-Z_]{0,39}$'),
    progress text CHECK (progress ~ '^[A-Z][A-Z_]{0,39}$'),
    changed_at timestamptz NOT NULL CHECK (
        changed_at BETWEEN TIMESTAMPTZ '0001-01-01T00:00:00Z'
                       AND TIMESTAMPTZ '9999-12-31T23:59:59.999999Z'
    ),
    search_text text CHECK (search_text IS NULL OR octet_length(search_text) BETWEEN 1 AND 1048576),
    notified_on date CHECK (notified_on BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    claimed_amount numeric(22,4) CHECK (claimed_amount >= 0),
    claimed_currency text CHECK (claimed_currency ~ '^[A-Z]{3}$'),
    payable_amount numeric(22,4) CHECK (payable_amount >= 0),
    work_search_text text,
    responsible_actor uuid,
    follow_up date CHECK (follow_up BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    payable_currency text CHECK (payable_currency ~ '^[A-Z]{3}$'),

    -- Printable ASCII without space: PRECIS IdentifierClass's ASCII subset (RFC 8264 4.2).
    CONSTRAINT reference_shape CHECK (case_reference ~ '^[!-~]{1,80}$'),
    CONSTRAINT erased_shape CHECK (
        ((state = 'ERASED') = (snapshot IS NULL))
        AND ((state = 'ERASED') = (status IS NULL))
        AND ((state = 'ERASED') = (progress IS NULL))
        AND ((state = 'ERASED') = (search_text IS NULL))
        AND ((state = 'ERASED') = (notified_on IS NULL))
        AND ((state = 'ERASED') = (claimed_amount IS NULL))
        AND ((state = 'ERASED') = (claimed_currency IS NULL))
        AND (state <> 'ERASED' OR (payable_amount IS NULL AND responsible_actor IS NULL AND follow_up IS NULL AND work_search_text IS NULL))
    ),
    CONSTRAINT payable_shape CHECK ((payable_amount IS NULL) = (payable_currency IS NULL)),
    CONSTRAINT snapshot_size CHECK (snapshot IS NULL OR octet_length(snapshot) BETWEEN 1 AND 32768)
);

-- One case per reference regardless of ASCII letter case; also the list order and keyset.
CREATE UNIQUE INDEX cases_reference_key ON claimcore.cases (upper(case_reference COLLATE "C"));

CREATE INDEX cases_search ON claimcore.cases USING gin (search_text public.gin_trgm_ops);
CREATE INDEX cases_work_search ON claimcore.cases USING gin (work_search_text public.gin_trgm_ops);

CREATE INDEX cases_facets ON claimcore.cases (state, status, progress);
CREATE INDEX cases_recent ON claimcore.cases (changed_at DESC, upper(case_reference COLLATE "C"));
CREATE INDEX cases_notified ON claimcore.cases (notified_on ASC NULLS LAST, upper(case_reference COLLATE "C"));

CREATE TABLE claimcore.actors (
    -- The operation ID of the authority entry that registered the actor.
    actor_id uuid PRIMARY KEY CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
    principal_kind text NOT NULL CHECK (principal_kind ~ '^[A-Z][A-Z_]{0,39}$'),
    issuer text NOT NULL CHECK (char_length(issuer) BETWEEN 1 AND 2048 AND claimcore.is_interchange_text(issuer)),
    subject text NOT NULL CHECK (char_length(subject) BETWEEN 1 AND 512 AND claimcore.is_interchange_text(subject)),
    enabled boolean NOT NULL,
    display_name text CHECK (display_name IS NULL OR (octet_length(display_name) BETWEEN 1 AND 1024 AND claimcore.is_interchange_text(display_name))),
    contact text CHECK (contact IS NULL OR (octet_length(contact) BETWEEN 1 AND 1024 AND claimcore.is_interchange_text(contact))),
    display_name_search text CHECK (display_name_search IS NULL OR (octet_length(display_name_search) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(display_name_search))),
    contact_search text CHECK (contact_search IS NULL OR (octet_length(contact_search) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(contact_search))),
    CONSTRAINT actor_profile_shape CHECK ((display_name IS NOT NULL OR contact IS NULL) AND ((display_name IS NULL)=(display_name_search IS NULL)) AND ((contact IS NULL)=(contact_search IS NULL))),
    UNIQUE (issuer, principal_kind, subject)
);

CREATE INDEX actors_display_name ON claimcore.actors USING gin (display_name_search public.gin_trgm_ops);
CREATE INDEX actors_contact ON claimcore.actors USING gin (contact_search public.gin_trgm_ops);

-- Bounded authentication hints, never ledger authority. Identity includes principal kind.
CREATE TABLE claimcore.access_request (
    principal_kind text NOT NULL CHECK (principal_kind IN ('HUMAN','SERVICE')),
    issuer text COLLATE "C" NOT NULL CHECK (char_length(issuer) BETWEEN 1 AND 2048 AND claimcore.is_interchange_text(issuer)),
    subject text COLLATE "C" NOT NULL CHECK (char_length(subject) BETWEEN 1 AND 512 AND claimcore.is_interchange_text(subject)),
    first_seen timestamptz NOT NULL,
    last_seen timestamptz NOT NULL CHECK (last_seen>=first_seen),
    name text CHECK (name IS NULL OR (char_length(name) BETWEEN 1 AND 200 AND claimcore.is_interchange_text(name))),
    preferred_username text CHECK (preferred_username IS NULL OR (char_length(preferred_username) BETWEEN 1 AND 200 AND claimcore.is_interchange_text(preferred_username))),
    email text CHECK (email IS NULL OR (char_length(email) BETWEEN 1 AND 200 AND claimcore.is_interchange_text(email))),
    PRIMARY KEY (issuer,principal_kind,subject)
);
CREATE INDEX access_request_seen ON claimcore.access_request(last_seen DESC,issuer,principal_kind,subject);


ALTER TABLE claimcore.cases ADD CONSTRAINT cases_responsible_actor
    FOREIGN KEY (responsible_actor) REFERENCES claimcore.actors (actor_id);
CREATE INDEX cases_responsible ON claimcore.cases (responsible_actor, upper(case_reference COLLATE "C")) WHERE responsible_actor IS NOT NULL;
CREATE INDEX cases_follow_up ON claimcore.cases (follow_up ASC NULLS LAST, upper(case_reference COLLATE "C"));
CREATE INDEX cases_due ON claimcore.cases (follow_up, upper(case_reference COLLATE "C")) WHERE state='ACTIVE' AND status='OPENED' AND follow_up IS NOT NULL;

-- Bounded independently revisioned work; OPEN establishes an empty revision-zero root.
CREATE TABLE claimcore.case_work (
    case_id uuid PRIMARY KEY REFERENCES claimcore.cases(case_id),
    revision bigint NOT NULL CHECK (revision BETWEEN 0 AND 9007199254740991),
    snapshot bytea CHECK (snapshot IS NULL OR octet_length(snapshot) BETWEEN 1 AND 32768),
    responsible_actor uuid REFERENCES claimcore.actors(actor_id),
    handling_summary text CHECK (handling_summary IS NULL OR (octet_length(handling_summary) BETWEEN 1 AND 4096 AND claimcore.is_interchange_text(handling_summary))),
    search_text text,
    changed_at timestamptz,
    recorded_by uuid REFERENCES claimcore.actors(actor_id),
    summary_changed_at timestamptz,
    summary_recorded_by uuid REFERENCES claimcore.actors(actor_id),
    CHECK ((handling_summary IS NULL AND summary_changed_at IS NULL AND summary_recorded_by IS NULL) OR (handling_summary IS NOT NULL AND summary_changed_at IS NOT NULL AND summary_recorded_by IS NOT NULL)),
    CHECK ((revision=0 AND changed_at IS NULL AND recorded_by IS NULL AND responsible_actor IS NULL AND handling_summary IS NULL) OR (revision>0 AND changed_at IS NOT NULL AND recorded_by IS NOT NULL)),
    CHECK (snapshot IS NOT NULL OR (responsible_actor IS NULL AND handling_summary IS NULL AND search_text IS NULL))
);

-- Identity and parent survive erasure; all action/date/state/search bytes are erasable.
CREATE TABLE claimcore.reminders (
    reminder_id uuid PRIMARY KEY CHECK (reminder_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.cases(case_id),
    revision bigint NOT NULL CHECK (revision BETWEEN 1 AND 9007199254740991),
    snapshot bytea CHECK (snapshot IS NULL OR octet_length(snapshot) BETWEEN 1 AND 32768),
    state text CHECK (state IN ('PENDING','COMPLETED','CANCELLED')),
    action text CHECK (action IS NULL OR (octet_length(action) BETWEEN 1 AND 1024 AND claimcore.is_interchange_text(action))),
    due date CHECK (due BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    search_text text,
    changed_at timestamptz NOT NULL,
    recorded_by uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    CHECK (((snapshot IS NULL) AND state IS NULL AND action IS NULL AND due IS NULL AND search_text IS NULL) OR (snapshot IS NOT NULL AND state IS NOT NULL AND action IS NOT NULL AND due IS NOT NULL AND search_text IS NOT NULL))
);
CREATE INDEX reminders_parent ON claimcore.reminders(case_id,state,due,reminder_id);
CREATE INDEX reminders_pending_due ON claimcore.reminders(due,reminder_id) WHERE state='PENDING';
CREATE INDEX reminders_state_due ON claimcore.reminders(state,due,reminder_id) WHERE snapshot IS NOT NULL;
CREATE INDEX ledger_case_activity ON claimcore.ledger(parent_case_id,sequence DESC) WHERE subject_kind IN ('WORK','REMINDER');
CREATE INDEX ledger_reminder_next ON claimcore.ledger(next_reminder_id,sequence DESC) WHERE next_reminder_id IS NOT NULL;

CREATE TABLE claimcore.grants (
    actor_id uuid NOT NULL REFERENCES claimcore.actors (actor_id),
    -- Role tokens only; the role-to-capability matrix lives in one F# table and never in SQL.
    role text NOT NULL CHECK (role ~ '^[A-Z][A-Z_]{0,39}$'),
    -- NULL grants the role across the installation; otherwise only on this case.
    case_id uuid REFERENCES claimcore.cases (case_id),
    CONSTRAINT grants_unique UNIQUE NULLS NOT DISTINCT (actor_id, role, case_id)
);

CREATE INDEX grants_case ON claimcore.grants (case_id) WHERE case_id IS NOT NULL;

-- Resource-scoped authority reads load an actor's installation grants (case_id IS NULL) and its
-- grants on the one case a request targets, never the actor's whole grant set.
CREATE INDEX grants_actor_case ON claimcore.grants (actor_id, case_id);

CREATE TABLE claimcore.sessions (
    session_key text PRIMARY KEY CHECK (session_key COLLATE "C" ~ '^[0-9a-f]{64}$'),
    protected_ticket bytea NOT NULL CHECK (octet_length(protected_ticket) BETWEEN 1 AND 1048576),
    payload_version integer NOT NULL CHECK (payload_version = 1),
    created_at timestamptz NOT NULL CHECK (isfinite(created_at)),
    absolute_expiry timestamptz NOT NULL CHECK (isfinite(absolute_expiry)),
    last_activity timestamptz NOT NULL CHECK (isfinite(last_activity)),
    idle_seconds double precision NOT NULL CHECK (idle_seconds > 0 AND idle_seconds < 'Infinity'::double precision),
    CHECK (absolute_expiry > created_at AND last_activity >= created_at)
);
CREATE INDEX sessions_expiry ON claimcore.sessions (absolute_expiry);

-- ---------------------------------------------------------------------------------------------
-- Runtime privileges: least privilege for the one role the service connects as. It can append to
-- the ledger but cannot change or remove a row except through erase_case_payload; it can
-- maintain projections; it cannot run DDL.
-- ---------------------------------------------------------------------------------------------
REVOKE ALL ON ALL TABLES IN SCHEMA claimcore FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA claimcore FROM PUBLIC;

GRANT USAGE ON SCHEMA claimcore TO claimcore_runtime;
GRANT SELECT ON claimcore.baseline_identity, claimcore.installation, claimcore.ledger_head, claimcore.register_maintenance
    TO claimcore_runtime;
GRANT SELECT, INSERT ON claimcore.ledger TO claimcore_runtime;
GRANT SELECT, INSERT, UPDATE ON claimcore.cases, claimcore.case_work, claimcore.reminders, claimcore.actors TO claimcore_runtime;
GRANT SELECT, INSERT, DELETE ON claimcore.grants TO claimcore_runtime;
GRANT SELECT,INSERT,UPDATE,DELETE ON claimcore.access_request TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.lock_ledger_head() TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.erase_case_payload(uuid) TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.erase_profile_payload(uuid) TO claimcore_runtime;

GRANT EXECUTE ON FUNCTION claimcore.is_interchange_text(text) TO claimcore_runtime;

GRANT SELECT, INSERT, UPDATE, DELETE ON claimcore.sessions TO claimcore_runtime;
