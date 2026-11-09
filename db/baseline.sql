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
    installation_format smallint NOT NULL CHECK (installation_format = 2),
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

-- ---------------------------------------------------------------------------------------------
-- One root advances the installation head. Effects and private owner compartments are its closed children.
CREATE TABLE claimcore.ledger (
    sequence bigint PRIMARY KEY CHECK (sequence BETWEEN 1 AND 9007199254740991),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash)=32),
    entry_hash bytea NOT NULL UNIQUE CHECK (octet_length(entry_hash)=32),
    installation_format smallint NOT NULL CHECK (installation_format=2),
    operation_id uuid NOT NULL UNIQUE CHECK (operation_id<>'00000000-0000-0000-0000-000000000000'),
    actor_id uuid NOT NULL CHECK (actor_id<>'00000000-0000-0000-0000-000000000000'),
    recorded_at timestamptz NOT NULL CHECK (recorded_at BETWEEN TIMESTAMPTZ '0001-01-01T00:00:00Z' AND TIMESTAMPTZ '9999-12-31T23:59:59.999999Z'),
    business_date date CHECK (business_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
    command jsonb NOT NULL CHECK (jsonb_typeof(command)='object'),
    CHECK ((command->>'kind'='AUTHORITY')=(business_date IS NULL))
);

CREATE TABLE claimcore.ledger_effects (
    operation_sequence bigint NOT NULL REFERENCES claimcore.ledger(sequence),
    effect_index integer NOT NULL CHECK (effect_index BETWEEN 0 AND 49),
    descriptor jsonb NOT NULL CHECK (jsonb_typeof(descriptor)='object'),
    subject_kind text GENERATED ALWAYS AS (CASE descriptor->>'kind' WHEN 'NOTE_ADDED' THEN 'NOTE' WHEN 'NOTE_REMOVED' THEN 'NOTE' WHEN 'REMINDER_CREATED' THEN 'REMINDER' WHEN 'REMINDER_CHANGED' THEN 'REMINDER' ELSE descriptor->>'kind' END) STORED,
    subject_id uuid GENERATED ALWAYS AS ((CASE descriptor->>'kind' WHEN 'AUTHORITY' THEN descriptor->>'targetActorId' WHEN 'NOTE_ADDED' THEN descriptor->>'noteId' WHEN 'NOTE_REMOVED' THEN descriptor->>'noteId' WHEN 'REMINDER_CREATED' THEN descriptor->>'reminderId' WHEN 'REMINDER_CHANGED' THEN descriptor->>'reminderId' ELSE descriptor->>'caseId' END)::uuid) STORED,
    act text GENERATED ALWAYS AS (CASE descriptor->>'kind' WHEN 'AUTHORITY' THEN descriptor->'change'->>'kind' WHEN 'WORK' THEN 'UPDATE_WORK' WHEN 'NOTE_ADDED' THEN 'ADD_NOTE' WHEN 'NOTE_REMOVED' THEN 'REMOVE_NOTE' WHEN 'REMINDER_CREATED' THEN 'CREATE' ELSE descriptor->>'act' END) STORED,
    case_revision bigint GENERATED ALWAYS AS ((descriptor->>'revision')::bigint) STORED,
    parent_case_id uuid GENERATED ALWAYS AS ((CASE WHEN descriptor->>'kind' IN ('WORK','NOTE_ADDED','NOTE_REMOVED','REMINDER_CREATED','REMINDER_CHANGED') THEN descriptor->>'caseId' END)::uuid) STORED,
    creation_reference text GENERATED ALWAYS AS (descriptor->>'creationReference') STORED,
    payment_id uuid GENERATED ALWAYS AS ((descriptor->'payment'->>'operationId')::uuid) STORED,
    PRIMARY KEY(operation_sequence,effect_index),
    CHECK (subject_kind IN ('CASE','AUTHORITY','WORK','NOTE','REMINDER')),
    CHECK (subject_id IS NOT NULL AND subject_id<>'00000000-0000-0000-0000-000000000000'),
    CHECK (case_revision IS NULL OR case_revision BETWEEN 1 AND 9007199254740991)
);
CREATE UNIQUE INDEX ledger_case_revision ON claimcore.ledger_effects(subject_kind,subject_id,case_revision);
CREATE UNIQUE INDEX ledger_creation_reference ON claimcore.ledger_effects(upper(creation_reference COLLATE "C")) WHERE creation_reference IS NOT NULL;
CREATE INDEX ledger_case_events ON claimcore.ledger_effects(subject_id,operation_sequence DESC,effect_index DESC) WHERE subject_kind='CASE';
CREATE INDEX ledger_authority_events ON claimcore.ledger_effects(subject_id,operation_sequence DESC,effect_index DESC) WHERE subject_kind='AUTHORITY';
CREATE INDEX ledger_case_activity ON claimcore.ledger_effects(parent_case_id,operation_sequence DESC,effect_index DESC) WHERE parent_case_id IS NOT NULL;

CREATE TABLE claimcore.ledger_compartments (
    operation_sequence bigint NOT NULL REFERENCES claimcore.ledger(sequence),
    ordinal integer NOT NULL CHECK (ordinal=0),
    owner_kind text NOT NULL CHECK (owner_kind IN ('CASE','ACTOR_PROFILE')),
    owner_id uuid NOT NULL CHECK (owner_id<>'00000000-0000-0000-0000-000000000000'),
    purpose text NOT NULL CHECK (purpose IN ('CASE','WORK','NOTE','REMINDER','PROFILE')),
    effect_indices integer[] NOT NULL CHECK (cardinality(effect_indices) BETWEEN 1 AND 2 AND effect_indices[1]>=0 AND (cardinality(effect_indices)=1 OR effect_indices[2]>effect_indices[1])),
    commitment bytea NOT NULL CHECK (octet_length(commitment)=32),
    nonce bytea UNIQUE,
    payload bytea,
    PRIMARY KEY(operation_sequence,ordinal),
    CHECK ((owner_kind='ACTOR_PROFILE')=(purpose='PROFILE')),
    CHECK ((nonce IS NULL AND payload IS NULL) OR (nonce IS NOT NULL AND payload IS NOT NULL AND octet_length(nonce)=32 AND octet_length(payload) BETWEEN 1 AND 32768))
);
CREATE INDEX ledger_compartment_owner ON claimcore.ledger_compartments(owner_kind,owner_id,operation_sequence);

CREATE TABLE claimcore.ledger_head (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    sequence bigint NOT NULL CHECK (sequence BETWEEN 0 AND 9007199254740991),
    entry_hash bytea NOT NULL CHECK (octet_length(entry_hash)=32)
);
INSERT INTO claimcore.ledger_head(sequence,entry_hash) VALUES(0,decode(repeat('00',32),'hex'));

CREATE FUNCTION claimcore.lock_ledger_head(OUT sequence bigint,OUT entry_hash bytea,OUT observed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    SELECT h.sequence,h.entry_hash INTO STRICT sequence,entry_hash FROM claimcore.ledger_head h WHERE singleton FOR UPDATE;
    observed_at:=clock_timestamp();
END;
$$;

CREATE FUNCTION claimcore.json_shape(value jsonb, names text[])
RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
    SELECT jsonb_typeof(value)='object' AND
        (SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(value) key)=
        (SELECT array_agg(name ORDER BY name) FROM unnest(names) name)
$$;

-- This routine describes structural closure, not business admission or financial decisions.
CREATE FUNCTION claimcore.operation_effects(command jsonb, operation uuid, executor uuid, root_sequence bigint)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE family text:=command->>'kind'; action text; target uuid; revision bigint; result jsonb; item jsonb; i integer;
BEGIN
    IF family='CASE' THEN
        IF NOT claimcore.json_shape(command,ARRAY['kind','caseId','reference','expectedRevision','act','selectedPayment','compartment']) THEN RAISE EXCEPTION 'invalid case command shape' USING ERRCODE='CC004'; END IF;
        target:=(command->>'caseId')::uuid; revision:=(command->>'expectedRevision')::bigint; action:=command->>'act';
        IF action NOT IN ('OPEN','AMEND_REGISTRATION','CORRECT','DECIDE','WITHDRAW_DECISION','RECORD_PAYMENT','REMOVE_PAYMENT','CLOSE','REOPEN','VOID','REINSTATE','ERASE')
           OR revision IS NULL OR revision<0 OR revision>=9007199254740991 OR (action='OPEN' AND (revision<>0 OR target<>operation)) OR (action<>'OPEN' AND revision=0)
           OR (command->>'reference') COLLATE "C" !~ '^[!-~]{1,80}$'
           OR (action='REMOVE_PAYMENT')<>(command->'selectedPayment'<>'null'::jsonb)
           OR command->'compartment' IS DISTINCT FROM (CASE WHEN action='ERASE' THEN 'null'::jsonb ELSE '0'::jsonb END)
        THEN RAISE EXCEPTION 'invalid case command structure' USING ERRCODE='CC004'; END IF;
        IF action='REMOVE_PAYMENT' THEN PERFORM (command->>'selectedPayment')::uuid; END IF;
        RETURN jsonb_build_array(jsonb_build_object('kind','CASE','caseId',target,'revision',revision+1,'act',action,'creationReference',CASE WHEN action='OPEN' THEN command->>'reference' END,'payment',CASE WHEN action='RECORD_PAYMENT' THEN jsonb_build_object('kind','RECORDED','operationId',operation) WHEN action='REMOVE_PAYMENT' THEN jsonb_build_object('kind','REMOVED','operationId',(command->>'selectedPayment')::uuid) ELSE 'null'::jsonb END));
    ELSIF family='WORK' THEN
        IF NOT claimcore.json_shape(command,ARRAY['kind','caseId','expectedRevision','compartment']) OR command->'compartment'<>'0'::jsonb THEN RAISE EXCEPTION 'invalid work command shape' USING ERRCODE='CC004'; END IF;
        revision:=(command->>'expectedRevision')::bigint;
        IF revision<0 OR revision>=9007199254740991 THEN RAISE EXCEPTION 'invalid work revision' USING ERRCODE='CC004'; END IF;
        RETURN jsonb_build_array(jsonb_build_object('kind','WORK','caseId',(command->>'caseId')::uuid,'revision',revision+1));
    ELSIF family='NOTE' THEN
        action:=command->>'act'; target:=(command->>'caseId')::uuid;
        IF action='ADD_NOTE' AND claimcore.json_shape(command,ARRAY['kind','caseId','act','compartment']) AND command->'compartment'='0'::jsonb THEN
            RETURN jsonb_build_array(jsonb_build_object('kind','NOTE_ADDED','caseId',target,'noteId',operation));
        ELSIF action='REMOVE_NOTE' AND claimcore.json_shape(command,ARRAY['kind','caseId','noteId','act','compartment']) AND command->'compartment'='null'::jsonb THEN
            RETURN jsonb_build_array(jsonb_build_object('kind','NOTE_REMOVED','caseId',target,'noteId',(command->>'noteId')::uuid));
        ELSE RAISE EXCEPTION 'invalid note command shape' USING ERRCODE='CC004'; END IF;
    ELSIF family='REMINDER' THEN
        IF NOT claimcore.json_shape(command,ARRAY['kind','caseId','act','reminderId','expectedRevision','compartment']) OR command->'compartment'<>'0'::jsonb THEN RAISE EXCEPTION 'invalid reminder command shape' USING ERRCODE='CC004'; END IF;
        target:=(command->>'caseId')::uuid; action:=command->>'act';
        IF action='CREATE' AND command->'reminderId'='null'::jsonb AND command->'expectedRevision'='null'::jsonb THEN
            RETURN jsonb_build_array(jsonb_build_object('kind','REMINDER_CREATED','caseId',target,'reminderId',operation,'revision',1));
        END IF;
        revision:=(command->>'expectedRevision')::bigint;
        IF action NOT IN ('UPDATE','COMPLETE','CANCEL','REOPEN','COMPLETE_AND_PLAN_NEXT') OR revision IS NULL OR revision<1 OR revision>=9007199254740991 OR command->>'reminderId' IS NULL THEN RAISE EXCEPTION 'invalid reminder change' USING ERRCODE='CC004'; END IF;
        result:=jsonb_build_array(jsonb_build_object('kind','REMINDER_CHANGED','caseId',target,'reminderId',(command->>'reminderId')::uuid,'revision',revision+1,'act',CASE WHEN action='COMPLETE_AND_PLAN_NEXT' THEN 'COMPLETE' ELSE action END));
        IF action='COMPLETE_AND_PLAN_NEXT' THEN
            IF (command->>'reminderId')::uuid=operation THEN RAISE EXCEPTION 'reminder identities overlap' USING ERRCODE='CC004'; END IF;
            result:=result||jsonb_build_array(jsonb_build_object('kind','REMINDER_CREATED','caseId',target,'reminderId',operation,'revision',1));
        END IF;
        RETURN result;
    ELSIF family='AUTHORITY' THEN
        IF NOT claimcore.json_shape(command,ARRAY['kind','targetActorId','principal','act']) OR NOT claimcore.json_shape(command->'principal',ARRAY['kind','issuer','subject'])
           OR command->'principal'->>'kind' NOT IN ('HUMAN','SERVICE') OR jsonb_typeof(command->'principal'->'issuer')<>'string' OR jsonb_typeof(command->'principal'->'subject')<>'string'
        THEN RAISE EXCEPTION 'invalid authority command shape' USING ERRCODE='CC004'; END IF;
        target:=(command->>'targetActorId')::uuid; action:=command->'act'->>'kind'; item:=command->'act';
        IF action IN ('REGISTER','ADMIT','PROVISION_OWNER') THEN
            IF target<>operation OR item->'profileCompartment'<>'0'::jsonb THEN RAISE EXCEPTION 'invalid registered actor identity' USING ERRCODE='CC004'; END IF;
            result:=jsonb_build_array(jsonb_build_object('kind','AUTHORITY','targetActorId',target,'change',jsonb_build_object('kind',CASE WHEN action='PROVISION_OWNER' THEN action ELSE 'REGISTER' END,'principal',command->'principal')),
                jsonb_build_object('kind','AUTHORITY','targetActorId',target,'change',jsonb_build_object('kind','SET_PROFILE','profileCompartment',0)));
            IF action='REGISTER' THEN
                IF NOT claimcore.json_shape(item,ARRAY['kind','profileCompartment']) THEN RAISE EXCEPTION 'invalid registration shape' USING ERRCODE='CC004'; END IF;
            ELSIF action='ADMIT' THEN
                IF NOT claimcore.json_shape(item,ARRAY['kind','profileCompartment','grants']) OR jsonb_typeof(item->'grants')<>'array' OR jsonb_array_length(item->'grants') NOT BETWEEN 1 AND 48 THEN RAISE EXCEPTION 'invalid admission shape' USING ERRCODE='CC004'; END IF;
                IF (SELECT count(*)<>count(DISTINCT value) FROM jsonb_array_elements(item->'grants')) THEN RAISE EXCEPTION 'duplicate admission grants' USING ERRCODE='CC004'; END IF;
                FOR item IN SELECT value FROM jsonb_array_elements(command->'act'->'grants') LOOP
                    IF NOT claimcore.json_shape(item,ARRAY['role','scope']) THEN RAISE EXCEPTION 'invalid grant shape' USING ERRCODE='CC004'; END IF;
                    result:=result||jsonb_build_array(jsonb_build_object('kind','AUTHORITY','targetActorId',target,'change',jsonb_build_object('kind','GRANT','role',item->'role','scope',item->'scope')));
                END LOOP;
            ELSE
                IF NOT claimcore.json_shape(item,ARRAY['kind','profileCompartment','roles']) OR jsonb_typeof(item->'roles')<>'array' OR jsonb_array_length(item->'roles')>4
                   OR root_sequence<>1 OR executor<>target OR command->'principal'->>'kind'<>'HUMAN'
                   OR EXISTS(SELECT 1 FROM jsonb_array_elements_text(item->'roles') role WHERE role NOT IN ('CASE_HANDLER','PAYMENT_RECORDER','DATA_STEWARD','AUDITOR'))
                   OR (SELECT count(*)<>count(DISTINCT value) FROM jsonb_array_elements(item->'roles'))
                THEN RAISE EXCEPTION 'invalid owner provisioning shape' USING ERRCODE='CC004'; END IF;
                FOR item IN SELECT value FROM jsonb_array_elements(command->'act'->'roles') LOOP
                    result:=result||jsonb_build_array(jsonb_build_object('kind','AUTHORITY','targetActorId',target,'change',jsonb_build_object('kind','GRANT','role',item,'scope',jsonb_build_object('kind','INSTALLATION'))));
                END LOOP;
            END IF;
            RETURN result;
        ELSIF action='SET_PROFILE' THEN
            IF NOT claimcore.json_shape(item,ARRAY['kind','profileCompartment']) OR item->'profileCompartment'<>'0'::jsonb THEN RAISE EXCEPTION 'invalid profile reference' USING ERRCODE='CC004'; END IF;
        ELSIF action IN ('GRANT','REVOKE') THEN
            IF NOT claimcore.json_shape(item,ARRAY['kind','role','scope']) THEN RAISE EXCEPTION 'invalid grant shape' USING ERRCODE='CC004'; END IF;
        ELSIF action IN ('ENABLE','DISABLE','ERASE_PROFILE') THEN
            IF NOT claimcore.json_shape(item,ARRAY['kind']) THEN RAISE EXCEPTION 'invalid authority act shape' USING ERRCODE='CC004'; END IF;
        ELSE RAISE EXCEPTION 'unknown authority act' USING ERRCODE='CC004'; END IF;
        RETURN jsonb_build_array(jsonb_build_object('kind','AUTHORITY','targetActorId',target,'change',item));
    ELSE RAISE EXCEPTION 'unknown operation family' USING ERRCODE='CC004'; END IF;
END;
$$;

CREATE FUNCTION claimcore.operation_compartments(command jsonb)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE family text:=command->>'kind'; action text; owner_kind text; owner_id uuid; purpose text; refs jsonb:='[0]'::jsonb;
BEGIN
    IF family='AUTHORITY' THEN
        action:=command->'act'->>'kind';
        IF action NOT IN ('REGISTER','ADMIT','PROVISION_OWNER','SET_PROFILE') THEN RETURN '[]'::jsonb; END IF;
        owner_kind:='ACTOR_PROFILE'; owner_id:=(command->>'targetActorId')::uuid; purpose:='PROFILE';
        IF action<>'SET_PROFILE' THEN refs:='[1]'::jsonb; END IF;
    ELSE
        owner_kind:='CASE';owner_id:=(command->>'caseId')::uuid;purpose:=family;
        IF (family='CASE' AND command->>'act'='ERASE') OR (family='NOTE' AND command->>'act'='REMOVE_NOTE') THEN RETURN '[]'::jsonb; END IF;
        IF family='REMINDER' AND command->>'act'='COMPLETE_AND_PLAN_NEXT' THEN refs:='[0,1]'::jsonb; END IF;
    END IF;
    RETURN jsonb_build_array(jsonb_build_object('ordinal',0,'ownerKind',owner_kind,'ownerId',owner_id,'purpose',purpose,'effects',refs));
END;
$$;

-- Immutable rows stay immutable for the schema owner too. The trusted owner can disable guards,
-- but runtime credentials have only the protected complete-append routine, never direct INSERT.
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
    IF NOT EXISTS(SELECT 1 FROM claimcore.ledger root WHERE root.sequence=NEW.operation_sequence AND root.xmin=pg_current_xact_id()::xid) THEN
        RAISE EXCEPTION 'accepted operations cannot acquire new children' USING ERRCODE='CC003';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER effects_insert_guard BEFORE INSERT ON claimcore.ledger_effects FOR EACH ROW EXECUTE FUNCTION claimcore.new_operation_child();
CREATE TRIGGER compartments_insert_guard BEFORE INSERT ON claimcore.ledger_compartments FOR EACH ROW EXECUTE FUNCTION claimcore.new_operation_child();

CREATE FUNCTION claimcore.compartment_guard()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF TG_OP<>'UPDATE' THEN RAISE EXCEPTION 'compartments are never removed' USING ERRCODE='CC003'; END IF;
    IF NEW.nonce IS NOT NULL OR NEW.payload IS NOT NULL OR
       (NEW.operation_sequence,NEW.ordinal,NEW.owner_kind,NEW.owner_id,NEW.purpose,NEW.effect_indices,NEW.commitment)
       IS DISTINCT FROM (OLD.operation_sequence,OLD.ordinal,OLD.owner_kind,OLD.owner_id,OLD.purpose,OLD.effect_indices,OLD.commitment)
       OR NOT EXISTS(SELECT 1 FROM claimcore.ledger_effects erased WHERE erased.subject_id=OLD.owner_id
             AND ((OLD.owner_kind='CASE' AND erased.subject_kind='CASE' AND erased.act='ERASE') OR (OLD.owner_kind='ACTOR_PROFILE' AND erased.subject_kind='AUTHORITY' AND erased.act='ERASE_PROFILE'))
             AND (erased.operation_sequence,erased.effect_index)>(OLD.operation_sequence,OLD.effect_indices[cardinality(OLD.effect_indices)]))
    THEN RAISE EXCEPTION 'private contents clear only under accepted owner erasure' USING ERRCODE='CC003'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER compartments_guard_rows BEFORE UPDATE OR DELETE ON claimcore.ledger_compartments FOR EACH ROW EXECUTE FUNCTION claimcore.compartment_guard();
CREATE TRIGGER compartments_guard_truncate BEFORE TRUNCATE ON claimcore.ledger_compartments FOR EACH STATEMENT EXECUTE FUNCTION claimcore.compartment_guard();

CREATE FUNCTION claimcore.complete_operation()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE root claimcore.ledger%ROWTYPE; actual jsonb; required jsonb; root_sequence bigint;
BEGIN
    IF TG_TABLE_NAME='ledger' THEN root_sequence:=NEW.sequence; ELSE root_sequence:=NEW.operation_sequence; END IF;
    SELECT * INTO STRICT root FROM claimcore.ledger WHERE sequence=root_sequence;
    required:=claimcore.operation_effects(root.command,root.operation_id,root.actor_id,root.sequence);
    SELECT jsonb_agg(descriptor ORDER BY effect_index) INTO actual FROM claimcore.ledger_effects WHERE operation_sequence=root.sequence;
    IF actual IS DISTINCT FROM required OR (SELECT array_agg(effect_index ORDER BY effect_index) FROM claimcore.ledger_effects WHERE operation_sequence=root.sequence) IS DISTINCT FROM ARRAY(SELECT generate_series(0,jsonb_array_length(required)-1)) THEN
        RAISE EXCEPTION 'operation effects are not a complete closed sequence' USING ERRCODE='CC004';
    END IF;
    SELECT COALESCE(jsonb_agg(jsonb_build_object('ordinal',ordinal,'ownerKind',owner_kind,'ownerId',owner_id,'purpose',purpose,'effects',to_jsonb(effect_indices)) ORDER BY ordinal),'[]'::jsonb) INTO actual FROM claimcore.ledger_compartments WHERE operation_sequence=root.sequence;
    IF actual IS DISTINCT FROM claimcore.operation_compartments(root.command) THEN RAISE EXCEPTION 'operation compartments do not close' USING ERRCODE='CC004'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER complete_operation_root AFTER INSERT ON claimcore.ledger DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.complete_operation();
CREATE CONSTRAINT TRIGGER complete_operation_effect AFTER INSERT ON claimcore.ledger_effects DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.complete_operation();
CREATE CONSTRAINT TRIGGER complete_operation_compartment AFTER INSERT ON claimcore.ledger_compartments DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.complete_operation();

CREATE FUNCTION claimcore.append_operation(p_installation uuid,p_sequence bigint,p_previous_hash bytea,p_hash bytea,p_format smallint,p_operation uuid,p_actor uuid,p_recorded_at timestamptz,p_business_date date,p_command jsonb,p_effects jsonb,p_compartments jsonb)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE head claimcore.ledger_head%ROWTYPE; expected jsonb; supplied jsonb; item jsonb; n integer;
BEGIN
    SELECT * INTO STRICT head FROM claimcore.ledger_head WHERE singleton FOR UPDATE;
    IF p_sequence<>head.sequence+1 OR p_previous_hash<>head.entry_hash THEN RAISE EXCEPTION 'operation does not extend the current head' USING ERRCODE='CC001'; END IF;
    IF p_installation IS DISTINCT FROM (SELECT installation_id FROM claimcore.installation) OR p_format<>2 OR jsonb_typeof(p_effects)<>'array' OR jsonb_typeof(p_compartments)<>'array' THEN RAISE EXCEPTION 'invalid operation envelope' USING ERRCODE='CC004'; END IF;
    expected:=claimcore.operation_effects(p_command,p_operation,p_actor,p_sequence);
    IF jsonb_array_length(p_effects)<>jsonb_array_length(expected) THEN RAISE EXCEPTION 'incomplete operation effects' USING ERRCODE='CC004'; END IF;
    FOR n IN 0..jsonb_array_length(expected)-1 LOOP
        item:=p_effects->n;
        IF NOT claimcore.json_shape(item,ARRAY['index','descriptor']) OR item->'index' IS DISTINCT FROM to_jsonb(n) OR item->'descriptor' IS DISTINCT FROM expected->n THEN RAISE EXCEPTION 'effect order or reference mismatch' USING ERRCODE='CC004'; END IF;
    END LOOP;
    expected:=claimcore.operation_compartments(p_command);
    IF jsonb_array_length(p_compartments)<>jsonb_array_length(expected) THEN RAISE EXCEPTION 'incomplete operation compartments' USING ERRCODE='CC004'; END IF;
    FOR n IN 0..jsonb_array_length(expected)-1 LOOP
        item:=p_compartments->n;
        IF NOT claimcore.json_shape(item,ARRAY['ordinal','ownerKind','ownerId','purpose','effects','commitment','nonce','payload'])
           OR item-ARRAY['commitment','nonce','payload'] IS DISTINCT FROM expected->n
           OR COALESCE(item->>'commitment','') !~ '^[0-9a-f]{64}$' OR COALESCE(item->>'nonce','') !~ '^[0-9a-f]{64}$'
           OR jsonb_typeof(item->'commitment')<>'string' OR jsonb_typeof(item->'nonce')<>'string' OR jsonb_typeof(item->'payload')<>'string'
           OR length(COALESCE(item->>'payload','')) NOT BETWEEN 2 AND 65536 OR length(item->>'payload')%2<>0 OR item->>'payload' ~ '[^0-9a-f]'
        THEN RAISE EXCEPTION 'invalid owner compartment structure' USING ERRCODE='CC004'; END IF;
    END LOOP;
    INSERT INTO claimcore.ledger(sequence,previous_hash,entry_hash,installation_format,operation_id,actor_id,recorded_at,business_date,command) VALUES(p_sequence,p_previous_hash,p_hash,p_format,p_operation,p_actor,p_recorded_at,p_business_date,p_command);
    FOR item IN SELECT value FROM jsonb_array_elements(p_effects) LOOP
        INSERT INTO claimcore.ledger_effects(operation_sequence,effect_index,descriptor) VALUES(p_sequence,(item->>'index')::integer,item->'descriptor');
    END LOOP;
    FOR item IN SELECT value FROM jsonb_array_elements(p_compartments) LOOP
        IF item->>'ownerKind'='CASE' AND EXISTS(SELECT 1 FROM claimcore.ledger_effects erased WHERE erased.subject_kind='CASE' AND erased.subject_id=(item->>'ownerId')::uuid AND erased.act='ERASE') THEN RAISE EXCEPTION 'erased case cannot acquire private contents' USING ERRCODE='CC004'; END IF;
        INSERT INTO claimcore.ledger_compartments(operation_sequence,ordinal,owner_kind,owner_id,purpose,effect_indices,commitment,nonce,payload)
        VALUES(p_sequence,(item->>'ordinal')::integer,item->>'ownerKind',(item->>'ownerId')::uuid,item->>'purpose',ARRAY(SELECT value::integer FROM jsonb_array_elements_text(item->'effects')),decode(item->>'commitment','hex'),decode(item->>'nonce','hex'),decode(item->>'payload','hex'));
    END LOOP;
    UPDATE claimcore.ledger_head SET sequence=p_sequence,entry_hash=p_hash WHERE singleton;
END;
$$;

CREATE FUNCTION claimcore.erase_case_payload(target_case_id uuid)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE erased bigint;
BEGIN
    IF NOT EXISTS(SELECT 1 FROM claimcore.ledger_effects WHERE subject_kind='CASE' AND subject_id=target_case_id AND act='ERASE') THEN RAISE EXCEPTION 'case has no accepted erasure' USING ERRCODE='CC002'; END IF;
    UPDATE claimcore.ledger_compartments SET payload=NULL,nonce=NULL WHERE owner_kind='CASE' AND owner_id=target_case_id AND payload IS NOT NULL;
    GET DIAGNOSTICS erased=ROW_COUNT;
    UPDATE claimcore.case_payments SET payment_date=NULL,paid_amount=NULL WHERE case_id=target_case_id;
    UPDATE claimcore.case_work SET snapshot=NULL,responsible_actor=NULL,handling_summary=NULL,search_text=NULL,summary_changed_at=NULL,summary_recorded_by=NULL WHERE case_id=target_case_id;
    UPDATE claimcore.reminders SET snapshot=NULL,state=NULL,action=NULL,due=NULL,search_text=NULL WHERE case_id=target_case_id;
    UPDATE claimcore.notes SET text=NULL WHERE case_id=target_case_id;
    RETURN erased;
END;
$$;
CREATE FUNCTION claimcore.erase_profile_payload(target_actor_id uuid)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE frontier bigint; erase_index integer; erased bigint;
BEGIN
    SELECT operation_sequence,effect_index INTO frontier,erase_index FROM claimcore.ledger_effects WHERE subject_kind='AUTHORITY' AND subject_id=target_actor_id AND act='ERASE_PROFILE' ORDER BY operation_sequence DESC,effect_index DESC LIMIT 1;
    IF frontier IS NULL THEN RAISE EXCEPTION 'actor has no accepted profile erasure' USING ERRCODE='CC002'; END IF;
    UPDATE claimcore.ledger_compartments SET payload=NULL,nonce=NULL WHERE owner_kind='ACTOR_PROFILE' AND owner_id=target_actor_id AND (operation_sequence,effect_indices[cardinality(effect_indices)])<(frontier,erase_index) AND payload IS NOT NULL;
    GET DIAGNOSTICS erased=ROW_COUNT;
    UPDATE claimcore.actors SET display_name=NULL,contact=NULL,display_name_search=NULL,contact_search=NULL WHERE actor_id=target_actor_id
       AND NOT EXISTS(SELECT 1 FROM claimcore.ledger_effects later WHERE later.subject_kind='AUTHORITY' AND later.act='SET_PROFILE' AND later.subject_id=target_actor_id AND (later.operation_sequence,later.effect_index)>(frontier,erase_index));
    RETURN erased;
END;
$$;

-- ---------------------------------------------------------------------------------------------
-- Projections. Derived from the ledger in the same transaction that appends to it; `verify`
-- recomputes them from the ledger and reports any divergence. They are never an authority.
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
GRANT EXECUTE ON FUNCTION claimcore.append_operation(uuid,bigint,bytea,bytea,smallint,uuid,uuid,timestamptz,date,jsonb,jsonb,jsonb) TO claimcore_runtime;
GRANT SELECT, INSERT, UPDATE ON claimcore.case_payments, claimcore.cases, claimcore.case_work, claimcore.reminders, claimcore.notes, claimcore.actors TO claimcore_runtime;
GRANT SELECT, INSERT, DELETE ON claimcore.grants TO claimcore_runtime;
GRANT SELECT,INSERT,UPDATE,DELETE ON claimcore.access_request TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.lock_ledger_head() TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.erase_case_payload(uuid) TO claimcore_runtime;
GRANT EXECUTE ON FUNCTION claimcore.erase_profile_payload(uuid) TO claimcore_runtime;

GRANT EXECUTE ON FUNCTION claimcore.is_interchange_text(text) TO claimcore_runtime;

GRANT SELECT, INSERT, UPDATE, DELETE ON claimcore.sessions TO claimcore_runtime;
