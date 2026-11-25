-- ClaimCore installation baseline.
--
-- Applied by `claimcore-server initialize` as the schema owner, inside one transaction, to a
-- database that has no `claimcore` schema. The administration tool inserts the `installation`
-- settings, including the disclosure retention period, and the baseline identity in that transaction.
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
    installation_format smallint NOT NULL CHECK (installation_format = 9),
    installation_id uuid NOT NULL
        CHECK (installation_id <> '00000000-0000-0000-0000-000000000000'),
    -- Immutable canonical IANA identifier, validated by the administration tool.
    business_time_zone text NOT NULL CHECK (
        char_length(business_time_zone) BETWEEN 1 AND 128
        AND business_time_zone = btrim(business_time_zone)
        AND business_time_zone !~ '[[:cntrl:]]'
    ),
    wrapped_data_key bytea NOT NULL CHECK (octet_length(wrapped_data_key)=60),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- Operational mode is owner-controlled and survives process restarts. It is not a ledger projection.
CREATE TABLE claimcore.register_maintenance (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    generation bigint NOT NULL CHECK (generation BETWEEN 0 AND 9007199254740991),
    pause_id uuid CHECK (pause_id <> '00000000-0000-0000-0000-000000000000'),
    CHECK (pause_id IS NULL OR generation>0)
);
INSERT INTO claimcore.register_maintenance(singleton,generation,pause_id) VALUES (true,0,NULL);

-- One immutable root and its ordered part versions advance the installation head.
CREATE TABLE claimcore.ledger (
    sequence bigint PRIMARY KEY CHECK (sequence BETWEEN 1 AND 9007199254740991),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash)=32),
    entry_hash bytea NOT NULL UNIQUE CHECK (octet_length(entry_hash)=32),
    installation_format smallint NOT NULL CHECK (installation_format=9),
    operation_id uuid NOT NULL UNIQUE CHECK (operation_id<>'00000000-0000-0000-0000-000000000000'),
    actor_id uuid CHECK (actor_id<>'00000000-0000-0000-0000-000000000000'),
    recorded_at timestamptz NOT NULL CHECK (recorded_at BETWEEN TIMESTAMPTZ '0001-01-01T00:00:00Z' AND TIMESTAMPTZ '9999-12-31T23:59:59.999999Z'),
    business_date date CHECK (business_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    aggregate_kind text NOT NULL CHECK (aggregate_kind IN ('FAMILY','PERSON')),
    task text COLLATE "C" NOT NULL CHECK (task ~ $task$\A[a-z]+(\.[a-z][A-Za-z]*)+\Z$task$),
    structure jsonb NOT NULL CHECK (jsonb_typeof(structure)='object'),
    CHECK ((aggregate_kind='PERSON')=(business_date IS NULL)),
    CONSTRAINT ledger_family_actor CHECK (actor_id IS NOT NULL OR aggregate_kind='PERSON')
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
    nonce bytea,
    nonce_fingerprint bytea UNIQUE,
    payload bytea STORAGE EXTERNAL,
    PRIMARY KEY(operation_sequence,ordinal),
    UNIQUE(operation_sequence,owner_kind,owner_id),
    CHECK ((nonce IS NULL AND nonce_fingerprint IS NULL AND payload IS NULL)
        OR (nonce IS NOT NULL AND nonce_fingerprint IS NOT NULL AND payload IS NOT NULL
            AND octet_length(nonce)=60 AND octet_length(nonce_fingerprint)=32
            AND octet_length(payload) BETWEEN 29 AND 32796))
);
ALTER TABLE claimcore.ledger_effects ADD CONSTRAINT effect_compartment
    FOREIGN KEY(operation_sequence,compartment) REFERENCES claimcore.ledger_compartments(operation_sequence,ordinal) DEFERRABLE INITIALLY DEFERRED;
CREATE INDEX ledger_compartment_owner ON claimcore.ledger_compartments(owner_kind,owner_id,operation_sequence);

-- Authoritative attachment payloads and their indexed accepting links. Neither depends on case
-- projections. A family erasure clears the private columns and keeps the accepting identities.
CREATE TABLE claimcore.attachment_blobs (
    case_id uuid NOT NULL,
    blob_id uuid NOT NULL,
    operation_sequence bigint NOT NULL,
    effect_index integer NOT NULL,
    length bigint,
    digest bytea,
    payload bytea STORAGE EXTERNAL,
    PRIMARY KEY(case_id,blob_id),
    UNIQUE(operation_sequence,effect_index),
    FOREIGN KEY(operation_sequence,effect_index) REFERENCES claimcore.ledger_effects(operation_sequence,effect_index),
    CHECK ((length IS NULL AND digest IS NULL AND payload IS NULL)
        OR (length IS NOT NULL AND digest IS NOT NULL AND payload IS NOT NULL
            AND length>=0 AND octet_length(payload)::bigint-28=length
            AND octet_length(digest)=32))
);
CREATE UNIQUE INDEX attachment_family_digest ON claimcore.attachment_blobs(case_id,digest) WHERE digest IS NOT NULL;

CREATE TABLE claimcore.attachment_references (
    case_id uuid NOT NULL,
    attachment_id uuid NOT NULL,
    operation_sequence bigint NOT NULL,
    effect_index integer NOT NULL,
    blob_id uuid,
    PRIMARY KEY(case_id,attachment_id),
    UNIQUE(operation_sequence,effect_index),
    FOREIGN KEY(operation_sequence,effect_index) REFERENCES claimcore.ledger_effects(operation_sequence,effect_index),
    FOREIGN KEY(case_id,blob_id) REFERENCES claimcore.attachment_blobs(case_id,blob_id)
);
CREATE INDEX attachment_blob_references ON claimcore.attachment_references(case_id,blob_id,operation_sequence DESC) WHERE blob_id IS NOT NULL;

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

CREATE TRIGGER attachment_blobs_insert_guard BEFORE INSERT ON claimcore.attachment_blobs FOR EACH ROW EXECUTE FUNCTION claimcore.new_operation_child();
CREATE TRIGGER attachment_references_insert_guard BEFORE INSERT ON claimcore.attachment_references FOR EACH ROW EXECUTE FUNCTION claimcore.new_operation_child();

CREATE FUNCTION claimcore.attachment_private_insert()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF EXISTS(SELECT 1 FROM claimcore.ledger_effects e
              WHERE e.erases_kind='FAMILY' AND e.erases_id=NEW.case_id) THEN
        RAISE EXCEPTION 'a cleared family cannot acquire attachment contents' USING ERRCODE='CC004';
    END IF;
    IF TG_TABLE_NAME='attachment_blobs' THEN
        IF NEW.payload IS NULL OR NEW.length IS NULL OR NEW.length<0
           OR octet_length(NEW.payload)::bigint-28<>NEW.length THEN
            RAISE EXCEPTION 'attachment ciphertext does not match its declared length' USING ERRCODE='CC004';
        END IF;
    ELSIF NEW.blob_id IS NULL THEN
        RAISE EXCEPTION 'an attachment requires a retained payload reference' USING ERRCODE='CC004';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER attachment_blobs_private_insert BEFORE INSERT ON claimcore.attachment_blobs FOR EACH ROW EXECUTE FUNCTION claimcore.attachment_private_insert();
CREATE TRIGGER attachment_references_private_insert BEFORE INSERT ON claimcore.attachment_references FOR EACH ROW EXECUTE FUNCTION claimcore.attachment_private_insert();

CREATE FUNCTION claimcore.attachment_payload_guard()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF TG_OP<>'UPDATE' THEN
        RAISE EXCEPTION 'attachment evidence rows are never removed' USING ERRCODE='CC003';
    END IF;
    IF (NEW.case_id,NEW.operation_sequence,NEW.effect_index) IS DISTINCT FROM
       (OLD.case_id,OLD.operation_sequence,OLD.effect_index)
       OR NOT EXISTS(SELECT 1 FROM claimcore.ledger_effects e
                     WHERE e.erases_kind='FAMILY' AND e.erases_id=OLD.case_id
                       AND (e.operation_sequence,e.effect_index)>(OLD.operation_sequence,OLD.effect_index)) THEN
        RAISE EXCEPTION 'attachment evidence clears only under a later accepted family erasure' USING ERRCODE='CC003';
    END IF;
    IF TG_TABLE_NAME='attachment_blobs' THEN
        IF NEW.blob_id<>OLD.blob_id OR NEW.length IS NOT NULL OR NEW.digest IS NOT NULL OR NEW.payload IS NOT NULL THEN
            RAISE EXCEPTION 'attachment payload may only be cleared' USING ERRCODE='CC003';
        END IF;
    ELSIF NEW.attachment_id<>OLD.attachment_id OR NEW.blob_id IS NOT NULL THEN
        RAISE EXCEPTION 'attachment reference may only be cleared' USING ERRCODE='CC003';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER attachment_blobs_protect BEFORE UPDATE OR DELETE ON claimcore.attachment_blobs FOR EACH ROW EXECUTE FUNCTION claimcore.attachment_payload_guard();
CREATE TRIGGER attachment_blobs_no_truncate BEFORE TRUNCATE ON claimcore.attachment_blobs FOR EACH STATEMENT EXECUTE FUNCTION claimcore.attachment_payload_guard();
CREATE TRIGGER attachment_references_protect BEFORE UPDATE OR DELETE ON claimcore.attachment_references FOR EACH ROW EXECUTE FUNCTION claimcore.attachment_payload_guard();
CREATE TRIGGER attachment_references_no_truncate BEFORE TRUNCATE ON claimcore.attachment_references FOR EACH STATEMENT EXECUTE FUNCTION claimcore.attachment_payload_guard();

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
    IF NEW.nonce IS NOT NULL OR NEW.nonce_fingerprint IS NOT NULL OR NEW.payload IS NOT NULL OR
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

CREATE FUNCTION claimcore.complete_attachment_effect(root_sequence bigint)
RETURNS void LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE effect claimcore.ledger_effects%ROWTYPE; link claimcore.attachment_references%ROWTYPE; blob claimcore.attachment_blobs%ROWTYPE;
        compartment_present boolean; erased boolean; introduces boolean;
BEGIN
    FOR effect IN SELECT * FROM claimcore.ledger_effects
        WHERE operation_sequence=root_sequence AND part_kind='ATTACHMENT'
    LOOP
        SELECT * INTO link FROM claimcore.attachment_references r
          WHERE (r.operation_sequence,r.effect_index)=(effect.operation_sequence,effect.effect_index);
        SELECT * INTO blob FROM claimcore.attachment_blobs b
          WHERE (b.operation_sequence,b.effect_index)=(effect.operation_sequence,effect.effect_index);

        introduces:=effect.retained->'introducesBlob'='true'::jsonb;
        IF effect.retained->'withdrawn'='true'::jsonb THEN
            IF link.attachment_id IS NOT NULL OR blob.blob_id IS NOT NULL THEN
                RAISE EXCEPTION 'attachment withdrawal cannot acquire payload evidence' USING ERRCODE='CC004';
            END IF;
        ELSE
            IF effect.aggregate_kind<>'FAMILY' OR introduces IS NULL OR effect.compartment IS NULL
               OR link.attachment_id IS DISTINCT FROM effect.part_id OR link.case_id IS DISTINCT FROM effect.aggregate_id
               OR introduces IS DISTINCT FROM (blob.blob_id IS NOT NULL)
               OR (introduces AND (blob.case_id,blob.blob_id) IS DISTINCT FROM (effect.aggregate_id,effect.part_id)) THEN
                RAISE EXCEPTION 'attachment evidence must bind to its accepting effect and family' USING ERRCODE='CC004';
            END IF;

            SELECT c.payload IS NOT NULL INTO compartment_present FROM claimcore.ledger_compartments c
              WHERE (c.operation_sequence,c.ordinal)=(root_sequence,effect.compartment);
            SELECT EXISTS(SELECT 1 FROM claimcore.ledger_effects e
                WHERE e.erases_kind='FAMILY' AND e.erases_id=effect.aggregate_id
                  AND (e.operation_sequence,e.effect_index)>(effect.operation_sequence,effect.effect_index))
              INTO erased;

            IF erased THEN
                IF link.blob_id IS NOT NULL OR compartment_present IS TRUE OR (introduces AND blob.payload IS NOT NULL) THEN
                    RAISE EXCEPTION 'erased attachment evidence retains private values' USING ERRCODE='CC003';
                END IF;
            ELSE
                SELECT * INTO blob FROM claimcore.attachment_blobs b
                  WHERE (b.case_id,b.blob_id)=(effect.aggregate_id,link.blob_id);
                IF blob.payload IS NULL OR compartment_present IS NOT TRUE
                   OR (introduces AND (blob.operation_sequence,blob.effect_index) IS DISTINCT FROM (effect.operation_sequence,effect.effect_index))
                   OR (NOT introduces AND (blob.operation_sequence,blob.effect_index)>=(effect.operation_sequence,effect.effect_index)) THEN
                    RAISE EXCEPTION 'attachment reference must agree with its retained payload' USING ERRCODE='CC004';
                END IF;
            END IF;
        END IF;
    END LOOP;

    IF EXISTS(SELECT 1 FROM claimcore.attachment_blobs b JOIN claimcore.ledger_effects e USING(operation_sequence,effect_index)
              WHERE b.operation_sequence=root_sequence AND e.part_kind<>'ATTACHMENT')
       OR EXISTS(SELECT 1 FROM claimcore.attachment_references r JOIN claimcore.ledger_effects e USING(operation_sequence,effect_index)
                 WHERE r.operation_sequence=root_sequence AND e.part_kind<>'ATTACHMENT') THEN
        RAISE EXCEPTION 'attachment evidence requires an attachment effect' USING ERRCODE='CC004';
    END IF;
END;
$$;

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
    PERFORM claimcore.complete_attachment_effect(root_sequence);
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER complete_operation_root AFTER INSERT ON claimcore.ledger DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.complete_operation();
CREATE CONSTRAINT TRIGGER complete_operation_effect AFTER INSERT ON claimcore.ledger_effects DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.complete_operation();
CREATE CONSTRAINT TRIGGER complete_operation_compartment AFTER INSERT ON claimcore.ledger_compartments DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.complete_operation();
CREATE CONSTRAINT TRIGGER complete_operation_attachment_blob AFTER INSERT ON claimcore.attachment_blobs DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.complete_operation();
CREATE CONSTRAINT TRIGGER complete_operation_attachment_reference AFTER INSERT ON claimcore.attachment_references DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.complete_operation();

-- Current list membership is rebuildable and carries no private descriptor.
CREATE TABLE claimcore.case_attachments (
    case_id uuid NOT NULL,
    attachment_id uuid NOT NULL,
    PRIMARY KEY(case_id,attachment_id),
    FOREIGN KEY(case_id,attachment_id) REFERENCES claimcore.attachment_references(case_id,attachment_id)
);

CREATE FUNCTION claimcore.append_operation(p_installation uuid,p_sequence bigint,p_previous_hash bytea,p_hash bytea,p_format smallint,p_operation uuid,p_actor uuid,p_recorded_at timestamptz,p_business_date date,p_aggregate_kind text,p_task text,p_structure jsonb,p_effects jsonb,p_compartments jsonb,p_attachment_payload bytea DEFAULT NULL)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE head claimcore.ledger_head%ROWTYPE; item jsonb; n integer;
        effect claimcore.ledger_effects%ROWTYPE; attachment_link jsonb; blob_identity uuid;
        blob_length bigint; blob_digest bytea; consumed_payload boolean:=false;
BEGIN
    SELECT * INTO STRICT head FROM claimcore.ledger_head WHERE singleton FOR UPDATE;
    IF p_sequence<>head.sequence+1 OR p_previous_hash<>head.entry_hash THEN RAISE EXCEPTION 'operation does not extend the current head' USING ERRCODE='CC001'; END IF;
    IF p_installation IS DISTINCT FROM (SELECT installation_id FROM claimcore.installation) OR p_format<>9
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
           OR item-ARRAY['aggregate','part','revision','naturalKey','retained','erases','compartment','attachmentLink']<>'{}'::jsonb
           OR (item ? 'attachmentLink' AND
               (item#>>'{part,kind}' IS DISTINCT FROM 'ATTACHMENT' OR item#>'{retained,withdrawn}'='true'::jsonb))
           OR NOT claimcore.json_shape(item->'aggregate',ARRAY['kind','id']) OR NOT claimcore.json_shape(item->'part',ARRAY['kind','id'])
           OR jsonb_typeof(item->'revision') IS DISTINCT FROM 'number' OR item->>'revision' !~ $integer$\A[0-9]+\Z$integer$
           OR (item ? 'erases' AND NOT claimcore.json_shape(item->'erases',ARRAY['kind','id'])) THEN
            RAISE EXCEPTION 'invalid part structure' USING ERRCODE='CC004';
        END IF;
        INSERT INTO claimcore.ledger_effects(operation_sequence,effect_index,aggregate_kind,aggregate_id,part_kind,part_id,revision,natural_key,retained,erases_kind,erases_id,compartment)
        VALUES(p_sequence,n,item->'aggregate'->>'kind',(item->'aggregate'->>'id')::uuid,item->'part'->>'kind',(item->'part'->>'id')::uuid,(item->>'revision')::bigint,item->>'naturalKey',item->'retained',item->'erases'->>'kind',(item->'erases'->>'id')::uuid,(item->>'compartment')::integer);
    END LOOP;
    FOR item IN SELECT value FROM jsonb_array_elements(p_compartments) LOOP
        IF NOT claimcore.json_shape(item,ARRAY['ordinal','owner','commitment','nonce','nonceFingerprint','payload']) OR NOT claimcore.json_shape(item->'owner',ARRAY['kind','id'])
           OR jsonb_typeof(item->'ordinal') IS DISTINCT FROM 'number' OR item->>'ordinal' !~ $integer$\A[0-9]+\Z$integer$
           OR jsonb_typeof(item->'commitment') IS DISTINCT FROM 'string' OR item->>'commitment' !~ $hash$\A[0-9a-f]{64}\Z$hash$
           OR jsonb_typeof(item->'nonce') IS DISTINCT FROM 'string' OR item->>'nonce' !~ $salt$\A[0-9a-f]{120}\Z$salt$
           OR jsonb_typeof(item->'nonceFingerprint') IS DISTINCT FROM 'string' OR item->>'nonceFingerprint' !~ $hash$\A[0-9a-f]{64}\Z$hash$
           OR jsonb_typeof(item->'payload') IS DISTINCT FROM 'string' OR length(item->>'payload') NOT BETWEEN 58 AND 65592 OR length(item->>'payload')%2<>0 OR item->>'payload' ~ '[^0-9a-f]' THEN
            RAISE EXCEPTION 'invalid owner compartment' USING ERRCODE='CC004';
        END IF;
        IF item->'owner'->>'kind'='FAMILY' AND EXISTS(SELECT 1 FROM claimcore.ledger_effects e WHERE e.erases_kind=item->'owner'->>'kind' AND e.erases_id=(item->'owner'->>'id')::uuid AND e.operation_sequence<p_sequence) THEN
            RAISE EXCEPTION 'a cleared permanent owner cannot acquire private values' USING ERRCODE='CC004';
        END IF;
        INSERT INTO claimcore.ledger_compartments(operation_sequence,ordinal,owner_kind,owner_id,commitment,nonce,nonce_fingerprint,payload)
        VALUES(p_sequence,(item->>'ordinal')::integer,item->'owner'->>'kind',(item->'owner'->>'id')::uuid,
               decode(item->>'commitment','hex'),decode(item->>'nonce','hex'),decode(item->>'nonceFingerprint','hex'),decode(item->>'payload','hex'));
    END LOOP;
    FOR effect IN SELECT * FROM claimcore.ledger_effects
        WHERE operation_sequence=p_sequence AND part_kind='ATTACHMENT' ORDER BY effect_index
    LOOP
        IF effect.retained->'withdrawn'='true'::jsonb THEN
            DELETE FROM claimcore.case_attachments WHERE (case_id,attachment_id)=(effect.aggregate_id,effect.part_id);
        ELSE
            -- These fields bind to the stored evidence at append time and are never
            -- copied into the permanent effect. Native verification authenticates the contents.
            attachment_link:=p_effects->effect.effect_index->'attachmentLink';
            IF (CASE WHEN jsonb_typeof(attachment_link)='object'
                     THEN claimcore.json_shape(attachment_link,ARRAY['blobId','length','keyedDigest'])
                     ELSE false END) IS NOT TRUE
               OR jsonb_typeof(attachment_link->'blobId') IS DISTINCT FROM 'string'
               OR attachment_link->>'blobId' !~ $uuid$\A[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}\Z$uuid$
               OR jsonb_typeof(attachment_link->'length') IS DISTINCT FROM 'number'
               OR attachment_link->>'length' !~ $length$\A[0-9]{1,16}\Z$length$
               OR jsonb_typeof(attachment_link->'keyedDigest') IS DISTINCT FROM 'string'
               OR attachment_link->>'keyedDigest' !~ $hash$\A[0-9a-f]{64}\Z$hash$ THEN
                RAISE EXCEPTION 'attachment addition requires a structural blob link' USING ERRCODE='CC004';
            END IF;
            blob_identity:=(attachment_link->>'blobId')::uuid;
            blob_length:=(attachment_link->>'length')::bigint;
            blob_digest:=decode(attachment_link->>'keyedDigest','hex');

            IF effect.retained->'introducesBlob'='true'::jsonb THEN
                IF consumed_payload OR p_attachment_payload IS NULL OR blob_identity<>effect.part_id THEN
                    RAISE EXCEPTION 'attachment addition requires its own declared blob' USING ERRCODE='CC004';
                END IF;
                INSERT INTO claimcore.attachment_blobs(case_id,blob_id,operation_sequence,effect_index,length,digest,payload)
                  VALUES(effect.aggregate_id,blob_identity,p_sequence,effect.effect_index,blob_length,blob_digest,p_attachment_payload);
                consumed_payload:=true;
            ELSIF NOT EXISTS(
                SELECT 1 FROM claimcore.attachment_blobs b
                  WHERE (b.case_id,b.blob_id)=(effect.aggregate_id,blob_identity)
                    AND (b.operation_sequence,b.effect_index)<(effect.operation_sequence,effect.effect_index)
                    AND b.length=blob_length AND b.digest=blob_digest AND b.payload IS NOT NULL
            ) THEN
                RAISE EXCEPTION 'attachment link does not match the earlier blob in its family' USING ERRCODE='CC004';
            END IF;
            INSERT INTO claimcore.attachment_references(case_id,attachment_id,operation_sequence,effect_index,blob_id)
              VALUES(effect.aggregate_id,effect.part_id,p_sequence,effect.effect_index,blob_identity);
            INSERT INTO claimcore.case_attachments(case_id,attachment_id) VALUES(effect.aggregate_id,effect.part_id);
        END IF;
    END LOOP;
    IF p_attachment_payload IS NOT NULL AND NOT consumed_payload THEN
        RAISE EXCEPTION 'attachment payload requires an accepting effect' USING ERRCODE='CC004';
    END IF;
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
    UPDATE claimcore.ledger_compartments c SET payload=NULL,nonce=NULL,nonce_fingerprint=NULL
      WHERE c.owner_kind=target_kind AND c.owner_id=target_id AND c.payload IS NOT NULL
        AND (c.operation_sequence,COALESCE((SELECT max(e.effect_index) FROM claimcore.ledger_effects e WHERE e.operation_sequence=c.operation_sequence AND e.compartment=c.ordinal),-1))<(frontier,erase_index);
    GET DIAGNOSTICS erased=ROW_COUNT;
    IF target_kind='FAMILY' THEN
        DELETE FROM claimcore.case_attachments WHERE case_id=target_id;
        UPDATE claimcore.attachment_references r SET blob_id=NULL WHERE r.case_id=target_id
          AND r.blob_id IS NOT NULL AND (r.operation_sequence,r.effect_index)<(frontier,erase_index);
        UPDATE claimcore.attachment_blobs b SET length=NULL,digest=NULL,payload=NULL WHERE b.case_id=target_id
          AND b.payload IS NOT NULL AND (b.operation_sequence,b.effect_index)<(frontier,erase_index);
    END IF;
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
    state text NOT NULL CHECK (state ~ '^[A-Z][A-Z_]{0,39}$'),

    snapshot bytea,
    status text CHECK (status ~ '^[A-Z][A-Z_]{0,39}$'),
    progress text CHECK (progress ~ '^[A-Z][A-Z_]{0,39}$'),
    changed_at timestamptz NOT NULL CHECK (
        changed_at BETWEEN TIMESTAMPTZ '0001-01-01T00:00:00Z'
                       AND TIMESTAMPTZ '9999-12-31T23:59:59.999999Z'
    ),
    search_text text CHECK (search_text IS NULL OR octet_length(search_text) BETWEEN 1 AND 1048576),
    received_on date CHECK (received_on BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    registered_on date NOT NULL CHECK (registered_on BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    closed_on date CHECK (closed_on BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    last_activity_on date NOT NULL CHECK (last_activity_on BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    claimed_amount numeric(22,4),
    claimed_currency text CHECK (claimed_currency ~ '^[A-Z]{3}$'),
    payable_amount numeric(22,4),
    payment_review boolean,
    outcome text CHECK (outcome ~ '^[A-Z][A-Z_]{0,39}$'),
    work_search_text text,
    responsible_actor uuid,
    follow_up date CHECK (follow_up BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    deadline_due date CHECK (deadline_due BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    payable_currency text CHECK (payable_currency ~ '^[A-Z]{3}$'),

    -- Printable ASCII without space: PRECIS IdentifierClass's ASCII subset (RFC 8264 4.2).
    -- The format bounds encoded storage; Domain owns reference and financial admission.
    CONSTRAINT reference_shape CHECK (case_reference ~ '^[!-~]+$' AND octet_length(case_reference)<=32768),
    CONSTRAINT private_shape CHECK ((snapshot IS NULL)=(status IS NULL) AND (snapshot IS NULL)=(progress IS NULL)
        AND (snapshot IS NULL)=(search_text IS NULL)
        AND (snapshot IS NOT NULL OR (received_on IS NULL AND claimed_amount IS NULL AND payment_review IS NULL AND outcome IS NULL AND payable_amount IS NULL AND responsible_actor IS NULL AND follow_up IS NULL AND deadline_due IS NULL AND work_search_text IS NULL))),
    CONSTRAINT claimed_shape CHECK ((claimed_amount IS NULL) = (claimed_currency IS NULL)),
    CONSTRAINT payable_shape CHECK ((payable_amount IS NULL) = (payable_currency IS NULL)),
    CONSTRAINT snapshot_size CHECK (snapshot IS NULL OR octet_length(snapshot) BETWEEN 1 AND 32768)
);

-- One case per reference regardless of ASCII letter case; also the list order and keyset.
CREATE UNIQUE INDEX cases_reference_key ON claimcore.cases (upper(case_reference COLLATE "C"));

CREATE INDEX cases_search ON claimcore.cases USING gin (search_text public.gin_trgm_ops);
CREATE INDEX cases_work_search ON claimcore.cases USING gin (work_search_text public.gin_trgm_ops);

CREATE INDEX cases_facets ON claimcore.cases (state, status, progress);
CREATE INDEX cases_recent ON claimcore.cases (changed_at DESC, upper(case_reference COLLATE "C"));
CREATE INDEX cases_received ON claimcore.cases (received_on ASC NULLS LAST, upper(case_reference COLLATE "C"));

-- Payment IDs and removal markers remain reserved. Parent erasure clears private amounts and dates.
CREATE TABLE claimcore.case_payments (
    payment_id uuid PRIMARY KEY CHECK (payment_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.cases(case_id),
    accepting_sequence bigint NOT NULL REFERENCES claimcore.ledger(sequence),
    effect_index integer NOT NULL CHECK (effect_index BETWEEN 0 AND 49),
    FOREIGN KEY(accepting_sequence,effect_index) REFERENCES claimcore.ledger_effects(operation_sequence,effect_index),
    payment_date date CHECK (payment_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    paid_amount numeric(22,4),
    currency text CHECK (currency ~ '^[A-Z]{3}$'),
    direction text CHECK (direction ~ '^[A-Z][A-Z_]{0,39}$'),
    reference text,
    payee_id uuid,
    removed boolean NOT NULL,
    CONSTRAINT payment_private_shape CHECK ((payment_date IS NULL) = (paid_amount IS NULL)
        AND (payment_date IS NULL) = (currency IS NULL) AND (payment_date IS NULL) = (direction IS NULL)
        AND (payment_date IS NOT NULL OR (reference IS NULL AND payee_id IS NULL))),
    UNIQUE (accepting_sequence, effect_index)
);
CREATE INDEX case_payments_current_order ON claimcore.case_payments(case_id,accepting_sequence DESC,effect_index DESC) WHERE NOT removed AND payment_date IS NOT NULL;
CREATE TABLE claimcore.case_payment_totals (
    case_id uuid NOT NULL REFERENCES claimcore.cases(case_id),
    currency text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    paid numeric(44,4) NOT NULL,
    returned numeric(44,4) NOT NULL,
    PRIMARY KEY (case_id,currency)
);

CREATE TABLE claimcore.actors (
    revision bigint NOT NULL CHECK (revision BETWEEN 1 AND 9007199254740991),
    -- The operation ID of the people entry that registered the actor.
    actor_id uuid PRIMARY KEY CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
    principal_key text COLLATE "C" NOT NULL UNIQUE CHECK (principal_key ~ $key$\A[0-9a-f]{64}\Z$key$),
    principal_kind text NOT NULL CHECK (principal_kind ~ '^[A-Z][A-Z_]{0,39}$'),
    issuer text NOT NULL CHECK (octet_length(issuer) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(issuer)),
    subject text NOT NULL CHECK (octet_length(subject) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(subject)),
    enabled boolean NOT NULL,
    display_name text CHECK (display_name IS NULL OR (octet_length(display_name) BETWEEN 1 AND 1024 AND claimcore.is_interchange_text(display_name))),
    contact text CHECK (contact IS NULL OR (octet_length(contact) BETWEEN 1 AND 1024 AND claimcore.is_interchange_text(contact))),
    display_name_search text CHECK (display_name_search IS NULL OR (octet_length(display_name_search) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(display_name_search))),
    contact_search text CHECK (contact_search IS NULL OR (octet_length(contact_search) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(contact_search))),
    digest_email text CHECK (digest_email IS NULL OR (octet_length(digest_email) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(digest_email))),
    CONSTRAINT actor_profile_shape CHECK ((display_name IS NOT NULL OR (contact IS NULL AND digest_email IS NULL)) AND ((display_name IS NULL)=(display_name_search IS NULL)) AND ((contact IS NULL)=(contact_search IS NULL)))
);

CREATE INDEX actors_display_name ON claimcore.actors USING gin (display_name_search public.gin_trgm_ops);
CREATE INDEX actors_contact ON claimcore.actors USING gin (contact_search public.gin_trgm_ops);

CREATE TABLE claimcore.digest_runs (
    -- An actor's creation operation survives projection rebuilds.
    actor_id uuid NOT NULL REFERENCES claimcore.ledger(operation_id),
    digest_date date NOT NULL,
    claimed_at timestamptz NOT NULL,
    outcome text CHECK (outcome IS NULL OR outcome ~ '^[A-Z][A-Z_]{0,39}$'),
    completed_at timestamptz,
    PRIMARY KEY (actor_id,digest_date),
    CHECK ((outcome IS NULL)=(completed_at IS NULL)),
    CHECK (completed_at IS NULL OR completed_at>=claimed_at)
);

-- Bounded authentication hints, never ledger evidence. Identity includes principal kind.
CREATE TABLE claimcore.access_request (
    principal_key text COLLATE "C" PRIMARY KEY CHECK (principal_key ~ $key$\A[0-9a-f]{64}\Z$key$),
    principal_kind text NOT NULL CHECK (principal_kind ~ '^[A-Z][A-Z_]{0,39}$'),
    issuer text COLLATE "C" NOT NULL CHECK (octet_length(issuer) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(issuer)),
    subject text COLLATE "C" NOT NULL CHECK (octet_length(subject) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(subject)),
    first_seen timestamptz NOT NULL,
    last_seen timestamptz NOT NULL CHECK (last_seen>=first_seen),
    name text CHECK (name IS NULL OR (char_length(name) BETWEEN 1 AND 200 AND claimcore.is_interchange_text(name))),
    preferred_username text CHECK (preferred_username IS NULL OR (char_length(preferred_username) BETWEEN 1 AND 200 AND claimcore.is_interchange_text(preferred_username))),
    email text CHECK (email IS NULL OR (char_length(email) BETWEEN 1 AND 200 AND claimcore.is_interchange_text(email))),
    email_verified boolean NOT NULL DEFAULT false CHECK (email IS NOT NULL OR NOT email_verified)
);
CREATE INDEX access_request_seen ON claimcore.access_request(last_seen DESC,principal_key);

-- The owner installation transaction supplies the Core retention period. A pruning caller cannot
-- shorten it; the runtime has read-only access to this installation value.
CREATE TABLE claimcore.disclosure_retention (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    months integer NOT NULL CHECK (months > 0)
);

-- Operational disclosure evidence is independent of ledger order and case erasure.
CREATE TABLE claimcore.disclosure_trail (
    id bigint GENERATED ALWAYS AS IDENTITY (MAXVALUE 9007199254740991)
        PRIMARY KEY CHECK (id BETWEEN 1 AND 9007199254740991),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp() CHECK (isfinite(recorded_at)),
    actor_id uuid NOT NULL CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
    kind text COLLATE "C" NOT NULL CHECK (kind ~ '^[A-Z][A-Z_]*$'),
    case_id uuid CHECK (case_id <> '00000000-0000-0000-0000-000000000000'),
    attachment_id uuid CHECK (attachment_id <> '00000000-0000-0000-0000-000000000000'),
    outcome text COLLATE "C" NOT NULL CHECK (outcome ~ '^[A-Z][A-Z_]*$'),
    export_query text COLLATE "C",
    export_selection jsonb,
    export_row_count integer,
    CHECK (
        (export_query IS NULL AND export_selection IS NULL AND export_row_count IS NULL)
        OR (export_query IS NOT NULL AND export_selection IS NOT NULL AND export_row_count IS NOT NULL
            AND jsonb_typeof(export_selection) = 'object' AND export_row_count >= 0)
    )
);

CREATE INDEX disclosure_trail_recorded ON claimcore.disclosure_trail(recorded_at,id);
CREATE INDEX disclosure_trail_actor ON claimcore.disclosure_trail(actor_id,recorded_at,id);
CREATE INDEX disclosure_trail_case ON claimcore.disclosure_trail(case_id,recorded_at,id);
CREATE INDEX disclosure_trail_kind ON claimcore.disclosure_trail(kind,recorded_at,id);

CREATE FUNCTION claimcore.disclosure_guard()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF TG_RELID='claimcore.disclosure_trail'::regclass AND TG_OP='DELETE'
       AND current_setting('claimcore.disclosure_pruning',true)='on'
       AND current_user=pg_get_userbyid((SELECT relowner FROM pg_catalog.pg_class WHERE oid=TG_RELID)) THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION 'disclosure evidence can only be deleted by retention pruning' USING ERRCODE='CC003';
END;
$$;
CREATE TRIGGER disclosure_trail_guard BEFORE UPDATE OR DELETE ON claimcore.disclosure_trail
FOR EACH ROW EXECUTE FUNCTION claimcore.disclosure_guard();
CREATE TRIGGER disclosure_trail_no_truncate BEFORE TRUNCATE ON claimcore.disclosure_trail
FOR EACH STATEMENT EXECUTE FUNCTION claimcore.disclosure_guard();
CREATE TRIGGER disclosure_retention_guard BEFORE UPDATE OR DELETE OR TRUNCATE ON claimcore.disclosure_retention
FOR EACH STATEMENT EXECUTE FUNCTION claimcore.disclosure_guard();

-- The application authorizes the actor in the snapshot that calls this reader. Give the
-- runtime only this keyset read rather than direct SELECT on the trail's storage columns.
CREATE FUNCTION claimcore.read_disclosure_trail(
    p_actor uuid, p_case uuid, p_kind text, p_from timestamptz, p_until timestamptz,
    p_before_time timestamptz, p_before_id bigint, p_limit integer
)
RETURNS SETOF claimcore.disclosure_trail LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    SELECT t.* FROM claimcore.disclosure_trail t
    WHERE (p_actor IS NULL OR t.actor_id=p_actor)
      AND (p_case IS NULL OR t.case_id=p_case)
      AND (p_kind IS NULL OR t.kind=p_kind)
      AND (p_from IS NULL OR t.recorded_at>=p_from)
      AND (p_until IS NULL OR t.recorded_at<p_until)
      AND (p_before_time IS NULL OR (t.recorded_at,t.id)<(p_before_time,p_before_id))
    ORDER BY t.recorded_at DESC,t.id DESC
    LIMIT p_limit
$$;

CREATE FUNCTION claimcore.prune_disclosure_trail(p_cutoff timestamptz, p_months integer)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE retention integer; removed bigint; prior_marker text;
BEGIN
    SELECT months INTO STRICT retention FROM claimcore.disclosure_retention WHERE singleton;
    IF p_months IS DISTINCT FROM retention OR p_cutoff IS NULL OR NOT isfinite(p_cutoff)
       OR p_cutoff>date_subtract(clock_timestamp(),make_interval(months => retention),'UTC') THEN
        RAISE EXCEPTION 'invalid disclosure retention cutoff' USING ERRCODE='CC005';
    END IF;

    -- Scope the marker to this deletion. Rollback restores it if deletion fails.
    prior_marker:=current_setting('claimcore.disclosure_pruning',true);
    PERFORM set_config('claimcore.disclosure_pruning','on',true);
    WITH candidates AS MATERIALIZED (
        SELECT id FROM claimcore.disclosure_trail
        WHERE recorded_at<p_cutoff ORDER BY recorded_at,id LIMIT 128 FOR UPDATE SKIP LOCKED
    )
    DELETE FROM claimcore.disclosure_trail t USING candidates c WHERE t.id=c.id;
    GET DIAGNOSTICS removed=ROW_COUNT;
    PERFORM set_config('claimcore.disclosure_pruning',prior_marker,true);
    RETURN removed;
END;
$$;


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
    state text CHECK (state ~ '^[A-Z][A-Z_]{0,39}$'),
    action text CHECK (action IS NULL OR (octet_length(action) BETWEEN 1 AND 1024 AND claimcore.is_interchange_text(action))),
    due date CHECK (due BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    kind text CHECK (kind ~ '^[A-Z][A-Z_]{0,39}$'),
    done_on date CHECK (done_on BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    search_text text,
    changed_at timestamptz NOT NULL,
    recorded_by uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    CHECK (((snapshot IS NULL) AND state IS NULL AND action IS NULL AND due IS NULL AND kind IS NULL AND done_on IS NULL AND search_text IS NULL) OR (snapshot IS NOT NULL AND state IS NOT NULL AND action IS NOT NULL AND due IS NOT NULL AND kind IS NOT NULL AND search_text IS NOT NULL))
);
CREATE INDEX reminders_parent ON claimcore.reminders(case_id,state,due,reminder_id);
CREATE INDEX reminders_state_due ON claimcore.reminders(state,due,reminder_id) WHERE snapshot IS NOT NULL;

CREATE TABLE claimcore.notes (
    note_id uuid PRIMARY KEY CHECK (note_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.cases(case_id),
    created_sequence bigint NOT NULL REFERENCES claimcore.ledger(sequence),
    created_effect_index integer NOT NULL CHECK (created_effect_index BETWEEN 0 AND 49),
    UNIQUE(created_sequence,created_effect_index),
    FOREIGN KEY(created_sequence,created_effect_index) REFERENCES claimcore.ledger_effects(operation_sequence,effect_index),
    text text CHECK (text IS NULL OR (octet_length(text) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(text)))
);
CREATE INDEX notes_current ON claimcore.notes(case_id,created_sequence DESC) WHERE text IS NOT NULL;

CREATE TABLE claimcore.case_parties (
    party_id uuid PRIMARY KEY CHECK (party_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.cases(case_id),
    revision bigint NOT NULL CHECK (revision BETWEEN 1 AND 9007199254740991),
    created_sequence bigint NOT NULL,
    created_effect_index integer NOT NULL CHECK (created_effect_index BETWEEN 0 AND 49),
    UNIQUE(case_id,party_id),
    UNIQUE(created_sequence,created_effect_index),
    FOREIGN KEY(created_sequence,created_effect_index) REFERENCES claimcore.ledger_effects(operation_sequence,effect_index),
    removed boolean NOT NULL,
    role text CHECK (role IS NULL OR role ~ '^[A-Z][A-Z_]{0,39}$'),
    name text CHECK (name IS NULL OR (octet_length(name) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(name))),
    details text CHECK (details IS NULL OR (octet_length(details) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(details))),
    CHECK ((role IS NULL AND name IS NULL AND details IS NULL) OR role IS NOT NULL)
);
CREATE INDEX case_parties_current ON claimcore.case_parties(case_id,created_sequence,created_effect_index) WHERE NOT removed;
ALTER TABLE claimcore.case_payments ADD CONSTRAINT payment_payee_ownership
    FOREIGN KEY (case_id,payee_id) REFERENCES claimcore.case_parties(case_id,party_id)
    DEFERRABLE INITIALLY DEFERRED;


CREATE TABLE claimcore.case_links (
    link_id uuid PRIMARY KEY CHECK (link_id <> '00000000-0000-0000-0000-000000000000'),
    source_case_id uuid NOT NULL REFERENCES claimcore.cases(case_id),
    target_case_id uuid REFERENCES claimcore.cases(case_id) DEFERRABLE INITIALLY DEFERRED,
    revision bigint NOT NULL CHECK (revision BETWEEN 1 AND 9007199254740991),
    removed boolean NOT NULL,
    note text CHECK (note IS NULL OR (octet_length(note) BETWEEN 1 AND 32768 AND claimcore.is_interchange_text(note))),
    CHECK (target_case_id IS NOT NULL OR note IS NULL),
    CHECK (NOT removed OR target_case_id IS NULL)
);
CREATE INDEX case_links_source ON claimcore.case_links(source_case_id);
CREATE INDEX case_links_target ON claimcore.case_links(target_case_id);

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
    principal_key text COLLATE "C" NOT NULL CHECK (principal_key ~ '^[0-9a-f]{64}$'),
    protected_ticket bytea NOT NULL CHECK (octet_length(protected_ticket) BETWEEN 1 AND 1048576),
    payload_version integer NOT NULL CHECK (payload_version = 1),
    created_at timestamptz NOT NULL CHECK (isfinite(created_at)),
    absolute_expiry timestamptz NOT NULL CHECK (isfinite(absolute_expiry)),
    last_activity timestamptz NOT NULL CHECK (isfinite(last_activity)),
    idle_seconds double precision NOT NULL CHECK (idle_seconds > 0 AND idle_seconds < 'Infinity'::double precision),
    CHECK (absolute_expiry > created_at AND last_activity >= created_at)
);
CREATE INDEX sessions_expiry ON claimcore.sessions (absolute_expiry);
CREATE INDEX sessions_principal ON claimcore.sessions (principal_key,session_key);

-- Deferred structural erasure safeguards check the final transaction, including every owned projection.
-- Ordinary note removal is covered by its accepted note effect; rebuilding projections is allowed.
CREATE FUNCTION claimcore.projection_erasure_integrity()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE family_id uuid; root claimcore.cases%ROWTYPE; erased boolean;
BEGIN
    IF TG_TABLE_NAME='ledger_effects' THEN
        family_id:=NEW.erases_id;
    ELSIF TG_TABLE_NAME='case_links' THEN
        family_id:=NEW.source_case_id;
    ELSE
        family_id:=NEW.case_id;
    END IF;
    IF TG_TABLE_NAME='ledger_effects' AND EXISTS(SELECT 1 FROM claimcore.ledger_compartments c WHERE c.owner_kind='FAMILY' AND c.owner_id=family_id AND c.payload IS NOT NULL) THEN
        RAISE EXCEPTION 'accepted family erasure must clear retained private values atomically' USING ERRCODE='CC003';
    END IF;
    IF TG_TABLE_NAME IN ('ledger_effects','case_attachments')
       AND EXISTS(SELECT 1 FROM claimcore.ledger_effects e WHERE e.erases_kind='FAMILY' AND e.erases_id=family_id)
       AND (EXISTS(SELECT 1 FROM claimcore.attachment_blobs b WHERE b.case_id=family_id AND b.payload IS NOT NULL)
            OR EXISTS(SELECT 1 FROM claimcore.attachment_references r WHERE r.case_id=family_id AND r.blob_id IS NOT NULL)
            OR EXISTS(SELECT 1 FROM claimcore.case_attachments a WHERE a.case_id=family_id)) THEN
        RAISE EXCEPTION 'accepted family erasure must clear all attachment contents atomically' USING ERRCODE='CC003';
    END IF;
    SELECT * INTO root FROM claimcore.cases WHERE case_id=family_id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT EXISTS(SELECT 1 FROM claimcore.ledger_effects e WHERE e.erases_kind='FAMILY' AND e.erases_id=family_id) INTO erased;
    IF erased THEN
        IF root.snapshot IS NOT NULL OR NOT EXISTS(SELECT 1 FROM claimcore.ledger_effects e
            WHERE e.part_kind='CASE' AND e.part_id=family_id AND e.revision=root.revision AND e.erases_kind='FAMILY' AND e.erases_id=family_id)
            OR EXISTS(SELECT 1 FROM claimcore.case_payments WHERE case_id=family_id AND payment_date IS NOT NULL)
            OR EXISTS(SELECT 1 FROM claimcore.case_payment_totals WHERE case_id=family_id)
            OR EXISTS(SELECT 1 FROM claimcore.case_work WHERE case_id=family_id AND (snapshot IS NOT NULL OR responsible_actor IS NOT NULL OR handling_summary IS NOT NULL OR search_text IS NOT NULL))
            OR EXISTS(SELECT 1 FROM claimcore.reminders WHERE case_id=family_id AND (snapshot IS NOT NULL OR state IS NOT NULL OR action IS NOT NULL OR due IS NOT NULL OR kind IS NOT NULL OR done_on IS NOT NULL OR search_text IS NOT NULL))
            OR EXISTS(SELECT 1 FROM claimcore.notes WHERE case_id=family_id AND text IS NOT NULL)
            OR EXISTS(SELECT 1 FROM claimcore.case_parties WHERE case_id=family_id AND role IS NOT NULL)
            OR EXISTS(SELECT 1 FROM claimcore.case_links WHERE source_case_id=family_id AND (target_case_id IS NOT NULL OR note IS NOT NULL))
            OR EXISTS(SELECT 1 FROM claimcore.ledger_compartments WHERE owner_kind='FAMILY' AND ledger_compartments.owner_id=family_id AND payload IS NOT NULL) THEN
            RAISE EXCEPTION 'accepted family erasure must clear every owned private value' USING ERRCODE='CC003';
        END IF;
    ELSE
        IF root.snapshot IS NULL
            OR EXISTS(SELECT 1 FROM claimcore.case_payments WHERE case_id=family_id AND payment_date IS NULL)
            OR EXISTS(SELECT 1 FROM claimcore.reminders WHERE case_id=family_id AND snapshot IS NULL)
            OR EXISTS(SELECT 1 FROM claimcore.case_parties WHERE case_id=family_id AND role IS NULL)
            OR EXISTS(SELECT 1 FROM claimcore.case_work WHERE case_id=family_id AND revision>0 AND snapshot IS NULL)
            OR EXISTS(SELECT 1 FROM claimcore.case_links WHERE source_case_id=family_id AND NOT removed AND target_case_id IS NULL)
            OR EXISTS(SELECT 1 FROM claimcore.notes n WHERE n.case_id=family_id AND n.text IS NULL AND NOT EXISTS(
                SELECT 1 FROM claimcore.ledger_effects e WHERE e.part_kind='NOTE' AND e.part_id=n.note_id
                  AND e.aggregate_kind='FAMILY' AND e.aggregate_id=family_id AND e.retained->>'removed'='true'
                  AND NOT EXISTS(SELECT 1 FROM claimcore.ledger_effects later WHERE later.part_kind=e.part_kind AND later.part_id=e.part_id AND later.revision>e.revision))) THEN
            RAISE EXCEPTION 'cleared projection requires accepted matching erasure or note-removal evidence' USING ERRCODE='CC003';
        END IF;
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER cases_erasure_integrity AFTER INSERT OR UPDATE ON claimcore.cases DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.projection_erasure_integrity();
CREATE CONSTRAINT TRIGGER payment_totals_erasure_integrity AFTER INSERT OR UPDATE ON claimcore.case_payment_totals DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.projection_erasure_integrity();
CREATE CONSTRAINT TRIGGER payments_erasure_integrity AFTER INSERT OR UPDATE ON claimcore.case_payments DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.projection_erasure_integrity();
CREATE CONSTRAINT TRIGGER work_erasure_integrity AFTER INSERT OR UPDATE ON claimcore.case_work DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.projection_erasure_integrity();
CREATE CONSTRAINT TRIGGER reminders_erasure_integrity AFTER INSERT OR UPDATE ON claimcore.reminders DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.projection_erasure_integrity();
CREATE CONSTRAINT TRIGGER parties_erasure_integrity AFTER INSERT OR UPDATE ON claimcore.case_parties DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.projection_erasure_integrity();
CREATE CONSTRAINT TRIGGER notes_erasure_integrity AFTER INSERT OR UPDATE ON claimcore.notes DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.projection_erasure_integrity();
CREATE CONSTRAINT TRIGGER case_links_erasure_integrity AFTER INSERT OR UPDATE ON claimcore.case_links DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.projection_erasure_integrity();
CREATE CONSTRAINT TRIGGER case_attachments_erasure_integrity AFTER INSERT OR UPDATE ON claimcore.case_attachments DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.projection_erasure_integrity();

CREATE CONSTRAINT TRIGGER accepted_family_erasure_integrity AFTER INSERT ON claimcore.ledger_effects DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.erases_kind='FAMILY') EXECUTE FUNCTION claimcore.projection_erasure_integrity();

CREATE FUNCTION claimcore.profile_erasure_integrity()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF EXISTS(SELECT 1 FROM claimcore.ledger_compartments c WHERE c.owner_kind=NEW.erases_kind AND c.owner_id=NEW.erases_id
        AND c.payload IS NOT NULL AND (c.operation_sequence,COALESCE((SELECT max(e.effect_index) FROM claimcore.ledger_effects e WHERE e.operation_sequence=c.operation_sequence AND e.compartment=c.ordinal),-1))<(NEW.operation_sequence,NEW.effect_index))
        OR EXISTS(SELECT 1 FROM claimcore.actors a WHERE a.actor_id=NEW.erases_id AND a.revision=NEW.revision
           AND (a.display_name IS NOT NULL OR a.contact IS NOT NULL OR a.digest_email IS NOT NULL OR a.display_name_search IS NOT NULL OR a.contact_search IS NOT NULL)) THEN
        RAISE EXCEPTION 'accepted profile erasure must clear covered private values atomically' USING ERRCODE='CC003';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER accepted_profile_erasure_integrity AFTER INSERT ON claimcore.ledger_effects DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.erases_kind='PROFILE') EXECUTE FUNCTION claimcore.profile_erasure_integrity();

-- Derived person rows must retain the private-presence shape proved by the latest immutable part.
-- Profile presence is the compartment association, never a policy flag copied into retained JSON.
CREATE FUNCTION claimcore.actor_profile_integrity()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE identity uuid; actor claimcore.actors%ROWTYPE; latest claimcore.ledger_effects%ROWTYPE;
        last_private claimcore.ledger_effects%ROWTYPE;
BEGIN
    IF TG_TABLE_NAME='actors' THEN identity:=NEW.actor_id; ELSE identity:=NEW.part_id; END IF;
    SELECT * INTO actor FROM claimcore.actors WHERE actor_id=identity;
    -- Projections can be deleted and reconstructed by maintenance.
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT * INTO latest FROM claimcore.ledger_effects WHERE part_kind='PERSON' AND part_id=identity ORDER BY revision DESC LIMIT 1;
    IF NOT FOUND OR latest.aggregate_kind<>'PERSON' OR latest.aggregate_id<>identity OR latest.revision<>actor.revision THEN
        RAISE EXCEPTION 'actor revision must match its latest accepted person part' USING ERRCODE='CC003';
    END IF;
    IF actor.display_name IS NOT NULL THEN
        IF latest.compartment IS NULL OR NOT EXISTS(SELECT 1 FROM claimcore.ledger_compartments c
            WHERE c.operation_sequence=latest.operation_sequence AND c.ordinal=latest.compartment
              AND c.owner_kind='PROFILE' AND c.owner_id=identity AND c.payload IS NOT NULL) THEN
            RAISE EXCEPTION 'actor private values require their current owner compartment' USING ERRCODE='CC003';
        END IF;
    ELSE
        IF latest.compartment IS NOT NULL THEN
            RAISE EXCEPTION 'actor private values cannot be cleared while its current part retains a profile' USING ERRCODE='CC003';
        END IF;
        SELECT * INTO last_private FROM claimcore.ledger_effects WHERE part_kind='PERSON' AND part_id=identity AND compartment IS NOT NULL
            ORDER BY operation_sequence DESC,effect_index DESC LIMIT 1;
        IF FOUND AND NOT EXISTS(SELECT 1 FROM claimcore.ledger_effects erased
            WHERE erased.erases_kind='PROFILE' AND erased.erases_id=identity
              AND (erased.operation_sequence,erased.effect_index)>(last_private.operation_sequence,last_private.effect_index)
              AND (erased.operation_sequence,erased.effect_index)<=(latest.operation_sequence,latest.effect_index)) THEN
            RAISE EXCEPTION 'actor private clearing requires an accepted erasure covering its latest private part' USING ERRCODE='CC003';
        END IF;
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER actor_profile_integrity AFTER INSERT OR UPDATE ON claimcore.actors DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.actor_profile_integrity();
CREATE CONSTRAINT TRIGGER accepted_person_profile_integrity AFTER INSERT ON claimcore.ledger_effects DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.part_kind='PERSON') EXECUTE FUNCTION claimcore.actor_profile_integrity();

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
GRANT SELECT ON claimcore.ledger,claimcore.ledger_effects,claimcore.ledger_compartments,claimcore.attachment_blobs,claimcore.attachment_references,claimcore.case_attachments TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.append_operation(uuid,bigint,bytea,bytea,smallint,uuid,uuid,timestamptz,date,text,text,jsonb,jsonb,jsonb,bytea) TO claimcore_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON claimcore.case_payment_totals TO claimcore_runtime;
GRANT SELECT, INSERT, UPDATE ON claimcore.case_payments, claimcore.cases, claimcore.case_work, claimcore.reminders, claimcore.notes, claimcore.case_parties, claimcore.case_links, claimcore.actors TO claimcore_runtime;
GRANT SELECT, INSERT, DELETE ON claimcore.grants TO claimcore_runtime;
GRANT SELECT,INSERT,UPDATE,DELETE ON claimcore.access_request, claimcore.digest_runs TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.lock_ledger_head() TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.advance_expiry_frontier(bigint) TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.clear_owner(text,uuid) TO claimcore_runtime;

GRANT EXECUTE ON FUNCTION claimcore.is_interchange_text(text) TO claimcore_runtime;


GRANT SELECT ON claimcore.disclosure_retention TO claimcore_runtime;
GRANT INSERT(actor_id,kind,case_id,attachment_id,outcome,export_query,export_selection,export_row_count)
    ON claimcore.disclosure_trail TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.read_disclosure_trail(uuid,uuid,text,timestamptz,timestamptz,timestamptz,bigint,integer) TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.prune_disclosure_trail(timestamptz,integer) TO claimcore_runtime;

GRANT SELECT, INSERT, DELETE ON claimcore.sessions TO claimcore_runtime;
-- Session identity and original deadlines are immutable for runtime writers.
GRANT UPDATE(protected_ticket,last_activity) ON claimcore.sessions TO claimcore_runtime;
