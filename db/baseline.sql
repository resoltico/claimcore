-- ClaimCore installation baseline.
--
-- Applied by `claimcore-server initialize` as the schema owner, inside one transaction, to a
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
-- Durable operation and private compartment framing: src/ClaimCore.RecordFormat/{OperationRoot,Compartment}.fs.
-- Narrative syntax is at most 4096 UTF-8 bytes; reminder action syntax is at most 1024 bytes.
-- Each owner compartment holds at most 32768 canonical private bytes.

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
    installation_format smallint NOT NULL CHECK (installation_format = 5),
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
    active boolean NOT NULL
);
INSERT INTO claimcore.register_maintenance(singleton,active) VALUES (true,false);

-- One immutable root and its ordered part versions advance the installation head.
CREATE TABLE claimcore.ledger (
    sequence bigint PRIMARY KEY CHECK (sequence BETWEEN 1 AND 9007199254740991),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash)=32),
    entry_hash bytea NOT NULL UNIQUE CHECK (octet_length(entry_hash)=32),
    installation_format smallint NOT NULL CHECK (installation_format=5),
    operation_id uuid NOT NULL UNIQUE CHECK (operation_id<>'00000000-0000-0000-0000-000000000000'),
    actor_id uuid NOT NULL CHECK (actor_id<>'00000000-0000-0000-0000-000000000000'),
    recorded_at timestamptz NOT NULL CHECK (recorded_at BETWEEN TIMESTAMPTZ '0001-01-01T00:00:00Z' AND TIMESTAMPTZ '9999-12-31T23:59:59.999999Z'),
    business_date date CHECK (business_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    aggregate_kind text NOT NULL CHECK (aggregate_kind IN ('FAMILY','PERSON')),
    task text COLLATE "C" NOT NULL CHECK (task ~ $task$\A[a-z]+(\.[a-z][A-Za-z]*)+\Z$task$),
    structure jsonb NOT NULL CHECK (jsonb_typeof(structure)='object'),
    CHECK ((aggregate_kind='PERSON')=(business_date IS NULL))
);
CREATE TABLE claimcore.ledger_effects (
    operation_sequence bigint NOT NULL REFERENCES claimcore.ledger(sequence),
    effect_index integer NOT NULL CHECK (effect_index BETWEEN 0 AND 49),
    aggregate_kind text NOT NULL CHECK (aggregate_kind IN ('FAMILY','PERSON')),
    aggregate_id uuid NOT NULL CHECK (aggregate_id<>'00000000-0000-0000-0000-000000000000'),
    part_kind text COLLATE "C" NOT NULL CHECK (part_kind ~ $part$\A[A-Z][A-Z_]{0,31}\Z$part$),
    part_id uuid NOT NULL CHECK (part_id<>'00000000-0000-0000-0000-000000000000'),
    revision bigint NOT NULL CHECK (revision BETWEEN 1 AND 9007199254740991),
    natural_key text CHECK (natural_key IS NULL OR claimcore.is_interchange_text(natural_key)),
    retained jsonb,
    erases_kind text CHECK (erases_kind IN ('FAMILY','PROFILE')),
    erases_id uuid CHECK (erases_id<>'00000000-0000-0000-0000-000000000000'),
    compartment integer CHECK (compartment BETWEEN 0 AND 1),
    PRIMARY KEY(operation_sequence,effect_index),
    UNIQUE(part_kind,part_id,revision),
    CHECK ((erases_kind IS NULL)=(erases_id IS NULL))
);
CREATE UNIQUE INDEX ledger_natural_key ON claimcore.ledger_effects(part_kind,upper(natural_key COLLATE "C")) WHERE natural_key IS NOT NULL;
CREATE INDEX ledger_aggregate_events ON claimcore.ledger_effects(aggregate_kind,aggregate_id,operation_sequence DESC,effect_index DESC);
CREATE INDEX ledger_part_events ON claimcore.ledger_effects(part_kind,part_id,revision);
CREATE INDEX ledger_owner_erasure ON claimcore.ledger_effects(erases_kind,erases_id,operation_sequence DESC,effect_index DESC) WHERE erases_kind IS NOT NULL;
CREATE TABLE claimcore.ledger_compartments (
    operation_sequence bigint NOT NULL REFERENCES claimcore.ledger(sequence),
    ordinal integer NOT NULL CHECK (ordinal BETWEEN 0 AND 1),
    owner_kind text NOT NULL CHECK (owner_kind IN ('FAMILY','PROFILE')),
    owner_id uuid NOT NULL CHECK (owner_id<>'00000000-0000-0000-0000-000000000000'),
    commitment bytea NOT NULL CHECK (octet_length(commitment)=32),
    nonce bytea UNIQUE,
    payload bytea,
    PRIMARY KEY(operation_sequence,ordinal),
    UNIQUE(operation_sequence,owner_kind,owner_id),
    CHECK ((nonce IS NULL AND payload IS NULL) OR (nonce IS NOT NULL AND payload IS NOT NULL AND octet_length(nonce)=32 AND octet_length(payload) BETWEEN 1 AND 32768))
);
ALTER TABLE claimcore.ledger_effects ADD CONSTRAINT effect_compartment
    FOREIGN KEY(operation_sequence,compartment) REFERENCES claimcore.ledger_compartments(operation_sequence,ordinal) DEFERRABLE INITIALLY DEFERRED;
CREATE INDEX ledger_compartment_owner ON claimcore.ledger_compartments(owner_kind,owner_id,operation_sequence);

CREATE TABLE claimcore.ledger_head (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    sequence bigint NOT NULL CHECK (sequence BETWEEN 0 AND 9007199254740991),
    entry_hash bytea NOT NULL CHECK (octet_length(entry_hash)=32),
    -- UTC .NET ticks preserve cutoff precision independently of PostgreSQL recording instants.
    expired_before_ticks bigint CHECK (expired_before_ticks BETWEEN 0 AND 3155378975999999999)
);
INSERT INTO claimcore.ledger_head(sequence,entry_hash) VALUES(0,decode(repeat('00',32),'hex'));

CREATE FUNCTION claimcore.lock_ledger_head(OUT sequence bigint,OUT entry_hash bytea,OUT observed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    SELECT h.sequence,h.entry_hash INTO STRICT sequence,entry_hash FROM claimcore.ledger_head h WHERE singleton FOR UPDATE;
    observed_at:=clock_timestamp();
END;
$$;

CREATE FUNCTION claimcore.expiry_frontier_monotonic()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF OLD.expired_before_ticks IS NOT NULL AND
       (NEW.expired_before_ticks IS NULL OR NEW.expired_before_ticks<OLD.expired_before_ticks) THEN
        RAISE EXCEPTION 'expiry frontier cannot decrease' USING ERRCODE='CC005';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER expiry_frontier_monotonic BEFORE UPDATE OF expired_before_ticks ON claimcore.ledger_head
FOR EACH ROW EXECUTE FUNCTION claimcore.expiry_frontier_monotonic();

CREATE FUNCTION claimcore.advance_expiry_frontier(p_ticks bigint)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE current_ticks bigint;
BEGIN
    IF p_ticks IS NULL OR p_ticks NOT BETWEEN 0 AND 3155378975999999999 THEN
        RAISE EXCEPTION 'invalid expiry frontier' USING ERRCODE='CC005';
    END IF;
    SELECT expired_before_ticks INTO STRICT current_ticks FROM claimcore.ledger_head WHERE singleton FOR UPDATE;
    UPDATE claimcore.ledger_head SET expired_before_ticks=GREATEST(current_ticks,p_ticks) WHERE singleton
        RETURNING expired_before_ticks INTO current_ticks;
    RETURN current_ticks;
END;
$$;

CREATE FUNCTION claimcore.json_shape(value jsonb,names text[])
RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
    SELECT jsonb_typeof(value)='object' AND
       (SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(value) key)=
       (SELECT array_agg(name ORDER BY name) FROM unnest(names) name)
$$;
CREATE FUNCTION claimcore.immutable_ledger_row()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'accepted operation structure is immutable' USING ERRCODE='CC003'; END;
$$;
CREATE TRIGGER ledger_guard_rows BEFORE UPDATE OR DELETE ON claimcore.ledger FOR EACH ROW EXECUTE FUNCTION claimcore.immutable_ledger_row();
CREATE TRIGGER ledger_guard_truncate BEFORE TRUNCATE ON claimcore.ledger FOR EACH STATEMENT EXECUTE FUNCTION claimcore.immutable_ledger_row();
CREATE TRIGGER effects_guard_rows BEFORE UPDATE OR DELETE ON claimcore.ledger_effects FOR EACH ROW EXECUTE FUNCTION claimcore.immutable_ledger_row();
CREATE TRIGGER effects_guard_truncate BEFORE TRUNCATE ON claimcore.ledger_effects FOR EACH STATEMENT EXECUTE FUNCTION claimcore.immutable_ledger_row();

CREATE FUNCTION claimcore.new_operation_child()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM claimcore.ledger root CROSS JOIN claimcore.ledger_head head
                  WHERE head.singleton AND root.sequence=NEW.operation_sequence
                    AND root.sequence=head.sequence+1 AND root.previous_hash=head.entry_hash) THEN
        RAISE EXCEPTION 'accepted operations cannot acquire new children' USING ERRCODE='CC003';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER effects_insert_guard BEFORE INSERT ON claimcore.ledger_effects FOR EACH ROW EXECUTE FUNCTION claimcore.new_operation_child();
CREATE TRIGGER compartments_insert_guard BEFORE INSERT ON claimcore.ledger_compartments FOR EACH ROW EXECUTE FUNCTION claimcore.new_operation_child();

CREATE FUNCTION claimcore.part_revision_chain()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE prior claimcore.ledger_effects%ROWTYPE;
BEGIN
    SELECT * INTO prior FROM claimcore.ledger_effects WHERE part_kind=NEW.part_kind AND part_id=NEW.part_id ORDER BY revision DESC LIMIT 1;
    IF FOUND THEN
        IF NEW.revision<>prior.revision+1 OR NEW.operation_sequence<=prior.operation_sequence THEN
            RAISE EXCEPTION 'part revision must immediately follow its predecessor' USING ERRCODE='CC004',CONSTRAINT='part_revision_chain';
        END IF;
        IF (NEW.aggregate_kind,NEW.aggregate_id) IS DISTINCT FROM (prior.aggregate_kind,prior.aggregate_id) THEN
            RAISE EXCEPTION 'a part cannot change its aggregate' USING ERRCODE='CC004',CONSTRAINT='part_aggregate_owner';
        END IF;
    ELSIF NEW.revision<>1 THEN
        RAISE EXCEPTION 'a part starts at revision one' USING ERRCODE='CC004',CONSTRAINT='part_revision_chain';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER part_revision_chain BEFORE INSERT ON claimcore.ledger_effects FOR EACH ROW EXECUTE FUNCTION claimcore.part_revision_chain();

CREATE FUNCTION claimcore.compartment_guard()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE last_reference integer;
BEGIN
    IF TG_OP<>'UPDATE' THEN RAISE EXCEPTION 'compartments are never removed' USING ERRCODE='CC003'; END IF;
    SELECT COALESCE(max(effect_index),-1) INTO last_reference FROM claimcore.ledger_effects WHERE operation_sequence=OLD.operation_sequence AND compartment=OLD.ordinal;
    IF NEW.nonce IS NOT NULL OR NEW.payload IS NOT NULL OR
       (NEW.operation_sequence,NEW.ordinal,NEW.owner_kind,NEW.owner_id,NEW.commitment)
       IS DISTINCT FROM (OLD.operation_sequence,OLD.ordinal,OLD.owner_kind,OLD.owner_id,OLD.commitment)
       OR NOT EXISTS(SELECT 1 FROM claimcore.ledger_effects erased WHERE erased.erases_kind=OLD.owner_kind AND erased.erases_id=OLD.owner_id
                     AND (erased.operation_sequence,erased.effect_index)>(OLD.operation_sequence,last_reference)) THEN
        RAISE EXCEPTION 'private contents clear only under a later accepted owner erasure' USING ERRCODE='CC003';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER compartments_guard_rows BEFORE UPDATE OR DELETE ON claimcore.ledger_compartments FOR EACH ROW EXECUTE FUNCTION claimcore.compartment_guard();
CREATE TRIGGER compartments_guard_truncate BEFORE TRUNCATE ON claimcore.ledger_compartments FOR EACH STATEMENT EXECUTE FUNCTION claimcore.compartment_guard();

CREATE FUNCTION claimcore.complete_operation()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE root claimcore.ledger%ROWTYPE; root_sequence bigint; first_effect claimcore.ledger_effects%ROWTYPE; n integer; ordinals integer[];
BEGIN
    IF TG_TABLE_NAME='ledger' THEN root_sequence:=NEW.sequence; ELSE root_sequence:=NEW.operation_sequence; END IF;
    SELECT * INTO STRICT root FROM claimcore.ledger WHERE sequence=root_sequence;
    IF EXISTS(SELECT 1 FROM claimcore.ledger_head head WHERE head.singleton AND
              (root.sequence>head.sequence OR (root.sequence=head.sequence AND root.entry_hash<>head.entry_hash))) THEN
        RAISE EXCEPTION 'operation must close the committed head extension' USING ERRCODE='CC004';
    END IF;
    SELECT count(*),array_agg(effect_index ORDER BY effect_index) INTO n,ordinals FROM claimcore.ledger_effects WHERE operation_sequence=root_sequence;
    IF n NOT BETWEEN 1 AND 50 OR ordinals IS DISTINCT FROM ARRAY(SELECT generate_series(0,n-1)) THEN
        RAISE EXCEPTION 'operation effects are not a complete ordered sequence' USING ERRCODE='CC004';
    END IF;
    SELECT * INTO STRICT first_effect FROM claimcore.ledger_effects WHERE operation_sequence=root_sequence AND effect_index=0;
    IF EXISTS(SELECT 1 FROM claimcore.ledger_effects e WHERE e.operation_sequence=root_sequence
              AND (e.aggregate_kind<>root.aggregate_kind OR e.aggregate_id<>first_effect.aggregate_id)) THEN
        RAISE EXCEPTION 'operation effects must share their aggregate' USING ERRCODE='CC004';
    END IF;
    SELECT count(*),array_agg(ordinal ORDER BY ordinal) INTO n,ordinals FROM claimcore.ledger_compartments WHERE operation_sequence=root_sequence;
    IF n>2 OR (n>0 AND ordinals IS DISTINCT FROM ARRAY(SELECT generate_series(0,n-1))) THEN
        RAISE EXCEPTION 'compartment ordinals are not contiguous' USING ERRCODE='CC004';
    END IF;
    IF EXISTS(SELECT 1 FROM claimcore.ledger_compartments c WHERE c.operation_sequence=root_sequence
              AND (c.owner_id<>first_effect.aggregate_id OR c.owner_kind<>CASE root.aggregate_kind WHEN 'FAMILY' THEN 'FAMILY' ELSE 'PROFILE' END)) THEN
        RAISE EXCEPTION 'compartment owner does not bind to its aggregate' USING ERRCODE='CC004';
    END IF;
    IF EXISTS(SELECT 1 FROM claimcore.ledger_effects e WHERE e.operation_sequence=root_sequence AND e.erases_kind IS NOT NULL
              AND (e.erases_id<>first_effect.aggregate_id OR e.erases_kind<>CASE root.aggregate_kind WHEN 'FAMILY' THEN 'FAMILY' ELSE 'PROFILE' END)) THEN
        RAISE EXCEPTION 'erasure owner does not bind to its aggregate' USING ERRCODE='CC004';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER complete_operation_root AFTER INSERT ON claimcore.ledger DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.complete_operation();
CREATE CONSTRAINT TRIGGER complete_operation_effect AFTER INSERT ON claimcore.ledger_effects DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.complete_operation();
CREATE CONSTRAINT TRIGGER complete_operation_compartment AFTER INSERT ON claimcore.ledger_compartments DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.complete_operation();

CREATE FUNCTION claimcore.append_operation(p_installation uuid,p_sequence bigint,p_previous_hash bytea,p_hash bytea,p_format smallint,p_operation uuid,p_actor uuid,p_recorded_at timestamptz,p_business_date date,p_aggregate_kind text,p_task text,p_structure jsonb,p_effects jsonb,p_compartments jsonb)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE head claimcore.ledger_head%ROWTYPE; item jsonb; n integer;
BEGIN
    SELECT * INTO STRICT head FROM claimcore.ledger_head WHERE singleton FOR UPDATE;
    IF p_sequence<>head.sequence+1 OR p_previous_hash<>head.entry_hash THEN RAISE EXCEPTION 'operation does not extend the current head' USING ERRCODE='CC001'; END IF;
    IF p_installation IS DISTINCT FROM (SELECT installation_id FROM claimcore.installation) OR p_format<>5
       OR NOT claimcore.json_shape(p_structure,ARRAY['binding','retained']) OR jsonb_typeof(p_structure->'binding') IS DISTINCT FROM 'object' OR jsonb_typeof(p_structure->'retained') IS DISTINCT FROM 'object'
       OR jsonb_typeof(p_effects) IS DISTINCT FROM 'array' OR jsonb_typeof(p_compartments) IS DISTINCT FROM 'array'
       OR jsonb_array_length(p_effects) NOT BETWEEN 1 AND 50 OR jsonb_array_length(p_compartments)>2 THEN
        RAISE EXCEPTION 'invalid operation envelope' USING ERRCODE='CC004';
    END IF;
    INSERT INTO claimcore.ledger(sequence,previous_hash,entry_hash,installation_format,operation_id,actor_id,recorded_at,business_date,aggregate_kind,task,structure)
        VALUES(p_sequence,p_previous_hash,p_hash,p_format,p_operation,p_actor,p_recorded_at,p_business_date,p_aggregate_kind,p_task,p_structure);
    FOR n IN 0..jsonb_array_length(p_effects)-1 LOOP
        item:=p_effects->n;
        IF jsonb_typeof(item) IS DISTINCT FROM 'object' OR NOT item ?& ARRAY['aggregate','part','revision']
           OR item-ARRAY['aggregate','part','revision','naturalKey','retained','erases','compartment']<>'{}'::jsonb
           OR NOT claimcore.json_shape(item->'aggregate',ARRAY['kind','id']) OR NOT claimcore.json_shape(item->'part',ARRAY['kind','id'])
           OR jsonb_typeof(item->'revision') IS DISTINCT FROM 'number' OR item->>'revision' !~ $integer$\A[0-9]+\Z$integer$
           OR (item ? 'erases' AND NOT claimcore.json_shape(item->'erases',ARRAY['kind','id'])) THEN
            RAISE EXCEPTION 'invalid part structure' USING ERRCODE='CC004';
        END IF;
        INSERT INTO claimcore.ledger_effects(operation_sequence,effect_index,aggregate_kind,aggregate_id,part_kind,part_id,revision,natural_key,retained,erases_kind,erases_id,compartment)
        VALUES(p_sequence,n,item->'aggregate'->>'kind',(item->'aggregate'->>'id')::uuid,item->'part'->>'kind',(item->'part'->>'id')::uuid,(item->>'revision')::bigint,item->>'naturalKey',item->'retained',item->'erases'->>'kind',(item->'erases'->>'id')::uuid,(item->>'compartment')::integer);
    END LOOP;
    FOR item IN SELECT value FROM jsonb_array_elements(p_compartments) LOOP
        IF NOT claimcore.json_shape(item,ARRAY['ordinal','owner','commitment','nonce','payload']) OR NOT claimcore.json_shape(item->'owner',ARRAY['kind','id'])
           OR jsonb_typeof(item->'ordinal') IS DISTINCT FROM 'number' OR item->>'ordinal' !~ $integer$\A[0-9]+\Z$integer$
           OR jsonb_typeof(item->'commitment') IS DISTINCT FROM 'string' OR item->>'commitment' !~ $hash$\A[0-9a-f]{64}\Z$hash$
           OR jsonb_typeof(item->'nonce') IS DISTINCT FROM 'string' OR item->>'nonce' !~ $hash$\A[0-9a-f]{64}\Z$hash$
           OR jsonb_typeof(item->'payload') IS DISTINCT FROM 'string' OR length(item->>'payload') NOT BETWEEN 2 AND 65536 OR length(item->>'payload')%2<>0 OR item->>'payload' ~ '[^0-9a-f]' THEN
            RAISE EXCEPTION 'invalid owner compartment' USING ERRCODE='CC004';
        END IF;
        IF item->'owner'->>'kind'='FAMILY' AND EXISTS(SELECT 1 FROM claimcore.ledger_effects e WHERE e.erases_kind=item->'owner'->>'kind' AND e.erases_id=(item->'owner'->>'id')::uuid AND e.operation_sequence<p_sequence) THEN
            RAISE EXCEPTION 'a cleared permanent owner cannot acquire private values' USING ERRCODE='CC004';
        END IF;
        INSERT INTO claimcore.ledger_compartments(operation_sequence,ordinal,owner_kind,owner_id,commitment,nonce,payload)
        VALUES(p_sequence,(item->>'ordinal')::integer,item->'owner'->>'kind',(item->'owner'->>'id')::uuid,decode(item->>'commitment','hex'),decode(item->>'nonce','hex'),decode(item->>'payload','hex'));
    END LOOP;
    UPDATE claimcore.ledger_head SET sequence=p_sequence,entry_hash=p_hash WHERE singleton;
END;
$$;

-- An old eraser cannot clear private data accepted after its frontier.
CREATE FUNCTION claimcore.clear_owner(target_kind text,target_id uuid)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE frontier bigint; erase_index integer; erased bigint;
BEGIN
    SELECT operation_sequence,effect_index INTO frontier,erase_index FROM claimcore.ledger_effects
      WHERE erases_kind=target_kind AND erases_id=target_id ORDER BY operation_sequence DESC,effect_index DESC LIMIT 1;
    IF frontier IS NULL THEN RAISE EXCEPTION 'owner has no accepted erasure' USING ERRCODE='CC002'; END IF;
    UPDATE claimcore.ledger_compartments c SET payload=NULL,nonce=NULL
      WHERE c.owner_kind=target_kind AND c.owner_id=target_id AND c.payload IS NOT NULL
        AND (c.operation_sequence,COALESCE((SELECT max(e.effect_index) FROM claimcore.ledger_effects e WHERE e.operation_sequence=c.operation_sequence AND e.compartment=c.ordinal),-1))<(frontier,erase_index);
    GET DIAGNOSTICS erased=ROW_COUNT;
    RETURN erased;
END;
$$;

-- ---------------------------------------------------------------------------------------------
-- Projections. Derived from the ledger in the same transaction that appends to it; `verify`
-- recomputes them from the ledger and reports any divergence. The ledger remains authoritative.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE claimcore.cases (
    -- The UUID of the operation which created the case.
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
    paid_amount numeric(22,4) CHECK (paid_amount >= 0),
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
        AND (state <> 'ERASED' OR (paid_amount IS NULL AND payable_amount IS NULL AND responsible_actor IS NULL AND follow_up IS NULL AND work_search_text IS NULL))
    ),
    CONSTRAINT payable_shape CHECK ((payable_amount IS NULL) = (payable_currency IS NULL)),
    CONSTRAINT paid_shape CHECK ((paid_amount IS NULL) = (payable_amount IS NULL) AND paid_amount <= payable_amount),
    CONSTRAINT snapshot_size CHECK (snapshot IS NULL OR octet_length(snapshot) BETWEEN 1 AND 32768)
);

-- One case per reference regardless of ASCII letter case; also the list order and keyset.
CREATE UNIQUE INDEX cases_reference_key ON claimcore.cases (upper(case_reference COLLATE "C"));

CREATE INDEX cases_search ON claimcore.cases USING gin (search_text public.gin_trgm_ops);
CREATE INDEX cases_work_search ON claimcore.cases USING gin (work_search_text public.gin_trgm_ops);

CREATE INDEX cases_facets ON claimcore.cases (state, status, progress);
CREATE INDEX cases_recent ON claimcore.cases (changed_at DESC, upper(case_reference COLLATE "C"));
CREATE INDEX cases_notified ON claimcore.cases (notified_on ASC NULLS LAST, upper(case_reference COLLATE "C"));

-- Payment IDs and removal markers remain reserved. Parent erasure clears private amounts and dates.
CREATE TABLE claimcore.case_payments (
    payment_id uuid PRIMARY KEY CHECK (payment_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.cases(case_id),
    accepting_sequence bigint NOT NULL REFERENCES claimcore.ledger(sequence),
    effect_index integer NOT NULL CHECK (effect_index BETWEEN 0 AND 49),
    FOREIGN KEY(accepting_sequence,effect_index) REFERENCES claimcore.ledger_effects(operation_sequence,effect_index),
    payment_date date CHECK (payment_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    paid_amount numeric(22,4) CHECK (paid_amount > 0),
    removed boolean NOT NULL,
    CONSTRAINT payment_private_pair CHECK ((payment_date IS NULL) = (paid_amount IS NULL)),
    UNIQUE (accepting_sequence, effect_index)
);
CREATE INDEX case_payments_current_order ON claimcore.case_payments(case_id,accepting_sequence DESC,effect_index DESC) WHERE NOT removed AND payment_date IS NOT NULL;
CREATE INDEX case_payments_current_dates ON claimcore.case_payments(case_id,payment_date,payment_id) WHERE NOT removed AND payment_date IS NOT NULL;

CREATE TABLE claimcore.actors (
    revision bigint NOT NULL CHECK (revision BETWEEN 1 AND 9007199254740991),
    -- The operation ID of the people entry that registered the actor.
    actor_id uuid PRIMARY KEY CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
    principal_key text COLLATE "C" NOT NULL UNIQUE CHECK (principal_key ~ $key$\A[0-9a-f]{64}\Z$key$),
    principal_kind text NOT NULL CHECK (principal_kind ~ '^[A-Z][A-Z_]{0,39}$'),
    issuer text NOT NULL CHECK (char_length(issuer) BETWEEN 1 AND 2048 AND claimcore.is_interchange_text(issuer)),
    subject text NOT NULL CHECK (char_length(subject) BETWEEN 1 AND 512 AND claimcore.is_interchange_text(subject)),
    enabled boolean NOT NULL,
    display_name text CHECK (display_name IS NULL OR (octet_length(display_name) BETWEEN 1 AND 1024 AND claimcore.is_interchange_text(display_name))),
    contact text CHECK (contact IS NULL OR (octet_length(contact) BETWEEN 1 AND 1024 AND claimcore.is_interchange_text(contact))),
    display_name_search text CHECK (display_name_search IS NULL OR (octet_length(display_name_search) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(display_name_search))),
    contact_search text CHECK (contact_search IS NULL OR (octet_length(contact_search) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(contact_search))),
    CONSTRAINT actor_profile_shape CHECK ((display_name IS NOT NULL OR contact IS NULL) AND ((display_name IS NULL)=(display_name_search IS NULL)) AND ((contact IS NULL)=(contact_search IS NULL)))
);

CREATE INDEX actors_display_name ON claimcore.actors USING gin (display_name_search public.gin_trgm_ops);
CREATE INDEX actors_contact ON claimcore.actors USING gin (contact_search public.gin_trgm_ops);

-- Bounded authentication hints, never ledger evidence. Identity includes principal kind.
CREATE TABLE claimcore.access_request (
    principal_key text COLLATE "C" PRIMARY KEY CHECK (principal_key ~ $key$\A[0-9a-f]{64}\Z$key$),
    principal_kind text NOT NULL CHECK (principal_kind IN ('HUMAN','SERVICE')),
    issuer text COLLATE "C" NOT NULL CHECK (char_length(issuer) BETWEEN 1 AND 2048 AND claimcore.is_interchange_text(issuer)),
    subject text COLLATE "C" NOT NULL CHECK (char_length(subject) BETWEEN 1 AND 512 AND claimcore.is_interchange_text(subject)),
    first_seen timestamptz NOT NULL,
    last_seen timestamptz NOT NULL CHECK (last_seen>=first_seen),
    name text CHECK (name IS NULL OR (char_length(name) BETWEEN 1 AND 200 AND claimcore.is_interchange_text(name))),
    preferred_username text CHECK (preferred_username IS NULL OR (char_length(preferred_username) BETWEEN 1 AND 200 AND claimcore.is_interchange_text(preferred_username))),
    email text CHECK (email IS NULL OR (char_length(email) BETWEEN 1 AND 200 AND claimcore.is_interchange_text(email)))
);
CREATE INDEX access_request_seen ON claimcore.access_request(last_seen DESC,principal_key);


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

CREATE TABLE claimcore.notes (
    note_id uuid PRIMARY KEY CHECK (note_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.cases(case_id),
    created_sequence bigint NOT NULL REFERENCES claimcore.ledger(sequence),
    created_effect_index integer NOT NULL CHECK (created_effect_index BETWEEN 0 AND 49),
    UNIQUE(created_sequence,created_effect_index),
    FOREIGN KEY(created_sequence,created_effect_index) REFERENCES claimcore.ledger_effects(operation_sequence,effect_index),
    text text CHECK (text IS NULL OR (char_length(text) BETWEEN 1 AND 2000 AND octet_length(text) <= 8192 AND claimcore.is_interchange_text(text)))
);
CREATE INDEX notes_current ON claimcore.notes(case_id,created_sequence DESC) WHERE text IS NOT NULL;

CREATE TABLE claimcore.grants (
    granted_sequence bigint NOT NULL REFERENCES claimcore.ledger (sequence),
    granted_effect_index integer NOT NULL CHECK (granted_effect_index BETWEEN 0 AND 49),
    FOREIGN KEY(granted_sequence,granted_effect_index) REFERENCES claimcore.ledger_effects(operation_sequence,effect_index),
    actor_id uuid NOT NULL REFERENCES claimcore.actors (actor_id),
    -- Role tokens only; the role-to-capability matrix lives in one F# table and never in SQL.
    role text NOT NULL CHECK (role ~ '^[A-Z][A-Z_]{0,39}$'),
    -- NULL grants the role across the installation; otherwise only on this case.
    case_id uuid REFERENCES claimcore.cases (case_id),
    CONSTRAINT grants_unique UNIQUE NULLS NOT DISTINCT (actor_id, role, case_id)
);

CREATE INDEX grants_case ON claimcore.grants (case_id) WHERE case_id IS NOT NULL;

-- Resource-scoped access reads load an actor's installation grants (case_id IS NULL) and its
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
-- Runtime privileges: the service can append only a complete operation through append_operation.
-- It cannot directly append or alter accepted structure; owner erasure clears private contents. It can
-- maintain projections; it cannot run DDL.
-- ---------------------------------------------------------------------------------------------
REVOKE ALL ON ALL TABLES IN SCHEMA claimcore FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA claimcore FROM PUBLIC;

GRANT USAGE ON SCHEMA claimcore TO claimcore_runtime;
GRANT SELECT ON claimcore.baseline_identity, claimcore.installation, claimcore.ledger_head, claimcore.register_maintenance
    TO claimcore_runtime;
GRANT SELECT ON claimcore.ledger,claimcore.ledger_effects,claimcore.ledger_compartments TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.append_operation(uuid,bigint,bytea,bytea,smallint,uuid,uuid,timestamptz,date,text,text,jsonb,jsonb,jsonb) TO claimcore_runtime;
GRANT SELECT, INSERT, UPDATE ON claimcore.case_payments, claimcore.cases, claimcore.case_work, claimcore.reminders, claimcore.notes, claimcore.actors TO claimcore_runtime;
GRANT SELECT, INSERT, DELETE ON claimcore.grants TO claimcore_runtime;
GRANT SELECT,INSERT,UPDATE,DELETE ON claimcore.access_request TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.lock_ledger_head() TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.advance_expiry_frontier(bigint) TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.clear_owner(text,uuid) TO claimcore_runtime;

GRANT EXECUTE ON FUNCTION claimcore.is_interchange_text(text) TO claimcore_runtime;

GRANT SELECT, INSERT, UPDATE, DELETE ON claimcore.sessions TO claimcore_runtime;
