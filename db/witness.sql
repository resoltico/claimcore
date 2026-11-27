-- Run in a dedicated database as its owner after provisioning claimcore_witness_runtime.
-- This database and its backups are independent of the ClaimCore register.
BEGIN;
CREATE SCHEMA claimcore_witness;
REVOKE ALL ON SCHEMA claimcore_witness FROM PUBLIC;

CREATE TABLE claimcore_witness.installations (
    installation uuid PRIMARY KEY,
    generation uuid NOT NULL UNIQUE,
    primary_id uuid NOT NULL UNIQUE,
    system_id bigint NOT NULL,
    timeline text NOT NULL CHECK (timeline ~ '^[0-9A-F]{8}$'),
    disclosure_lsn pg_lsn,
    admitted_sequence bigint NOT NULL CHECK (admitted_sequence>0),
    admitted_hash bytea NOT NULL CHECK (octet_length(admitted_hash)=32),
    expiry_floor_ticks bigint CHECK (expiry_floor_ticks BETWEEN 0 AND 3155378975999999999),
    credential_floor timestamptz CHECK (credential_floor IS NULL OR isfinite(credential_floor)),
    transition uuid UNIQUE,
    latest_dispatch date,
    suppress_through date,
    CHECK (suppress_through IS NULL OR isfinite(suppress_through)),
    CHECK (latest_dispatch IS NULL OR isfinite(latest_dispatch))
);

-- Fencing reserves a transition permanently and records the old branch. The
-- coverage decision is open until its digest is durably sealed.
CREATE TABLE claimcore_witness.transitions (
    transition uuid PRIMARY KEY CHECK (transition <> '00000000-0000-0000-0000-000000000000'),
    installation uuid NOT NULL REFERENCES claimcore_witness.installations,
    prior_generation uuid UNIQUE,
    prior_primary_id uuid,
    system_id bigint,
    timeline text CHECK (timeline ~ '^[0-9A-F]{8}$'),
    required_lsn pg_lsn,
    required_sequence bigint CHECK (required_sequence>0),
    required_hash bytea CHECK (octet_length(required_hash)=32),
    required_expiry_ticks bigint CHECK (required_expiry_ticks BETWEEN 0 AND 3155378975999999999),
    gap_reason text CHECK (gap_reason IN ('WAL_UNAVAILABLE', 'DISCONTINUOUS_HISTORY')),
    gap_recorded_at timestamptz,
    sealed_digest bytea CHECK (octet_length(sealed_digest)=32),
    sealed_at timestamptz,
    sealed_target uuid UNIQUE,
    sealed_approval bytea CHECK (octet_length(sealed_approval)=32),
    checkpoint_digest bytea CHECK (octet_length(checkpoint_digest)=32),
    supersedes uuid,
    fence_origin uuid,
    superseded_at timestamptz,
    UNIQUE (installation,transition),
    UNIQUE (installation,supersedes),
    FOREIGN KEY (installation,supersedes) REFERENCES claimcore_witness.transitions(installation,transition),
    FOREIGN KEY (installation,fence_origin) REFERENCES claimcore_witness.transitions(installation,transition),
    CHECK ((supersedes IS NULL)=(fence_origin IS NULL)),
    CHECK ((supersedes IS NULL)=(superseded_at IS NULL)),
    CHECK (supersedes IS DISTINCT FROM transition AND fence_origin IS DISTINCT FROM transition),
    CHECK (num_nonnulls(prior_generation,prior_primary_id,system_id,timeline,required_sequence,required_hash)
        = CASE WHEN fence_origin IS NULL THEN 6 ELSE 0 END),
    CHECK (required_lsn IS NULL OR fence_origin IS NULL),
    CHECK (required_expiry_ticks IS NULL OR fence_origin IS NULL),
    CHECK ((gap_reason IS NULL)=(gap_recorded_at IS NULL)),
    CHECK (gap_reason IS NULL OR required_lsn IS NOT NULL),
    CHECK (gap_reason IS NULL OR fence_origin IS NULL),
    CHECK ((sealed_digest IS NULL)=(sealed_at IS NULL)),
    CHECK ((sealed_digest IS NULL)=(sealed_target IS NULL)),
    CHECK ((sealed_digest IS NULL)=(sealed_approval IS NULL)),
    CHECK ((checkpoint_digest IS NOT NULL)=(fence_origin IS NULL AND sealed_digest IS NOT NULL))
);

ALTER TABLE claimcore_witness.installations
    ADD FOREIGN KEY (installation,transition)
    REFERENCES claimcore_witness.transitions(installation,transition);

CREATE VIEW claimcore_witness.gaps AS
    SELECT installation,transition,prior_generation,system_id,timeline,required_lsn,
           gap_reason AS reason,gap_recorded_at AS recorded_at
    FROM claimcore_witness.transitions
    WHERE gap_reason IS NOT NULL;

-- Each successor has its own preparation seal and refers to the original
-- coverage decision and checkpoint, including any gap recorded before sealing.
CREATE VIEW claimcore_witness.fence_decisions AS
    SELECT t.installation,t.transition,origin AS decision
    FROM claimcore_witness.transitions t
    JOIN claimcore_witness.transitions origin
        ON origin.installation=t.installation AND origin.transition=coalesce(t.fence_origin,t.transition);

CREATE FUNCTION claimcore_witness.is_active(uuid,uuid,bigint,text,uuid,timestamptz)
RETURNS boolean LANGUAGE sql SECURITY DEFINER STABLE SET search_path=pg_catalog AS $$
    SELECT EXISTS (
        SELECT FROM claimcore_witness.installations
        WHERE installation=$1 AND generation=$2 AND system_id=$3 AND timeline=$4 AND primary_id=$5
            AND credential_floor IS NOT DISTINCT FROM $6 AND transition IS NULL
    )
$$;

CREATE FUNCTION claimcore_witness.acknowledge_disclosure(uuid,uuid,bigint,text,uuid,pg_lsn)
RETURNS boolean LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
    WITH acknowledged AS (
        UPDATE claimcore_witness.installations
        SET disclosure_lsn=greatest(disclosure_lsn,$6)
        WHERE installation=$1 AND generation=$2 AND system_id=$3 AND timeline=$4 AND primary_id=$5
            AND transition IS NULL AND $6 > '0/0'::pg_lsn
        RETURNING 1
    )
    SELECT EXISTS(SELECT FROM acknowledged)
$$;

CREATE FUNCTION claimcore_witness.admit_dispatch(uuid,uuid,bigint,text,uuid,date)
RETURNS boolean LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
    WITH admitted AS (
        UPDATE claimcore_witness.installations
        SET latest_dispatch=$6
        WHERE installation=$1 AND generation=$2 AND system_id=$3 AND timeline=$4 AND primary_id=$5
            AND transition IS NULL AND isfinite($6)
            AND (suppress_through IS NULL OR  $6 > suppress_through)
            AND (latest_dispatch IS NULL OR  $6 >= latest_dispatch)
        RETURNING 1
    )
    SELECT EXISTS(SELECT FROM admitted)
$$;

CREATE FUNCTION claimcore_witness.fence(p_installation uuid,p_generation uuid,p_transition uuid)
RETURNS boolean LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE
    current_primary claimcore_witness.installations%ROWTYPE;
BEGIN
    SELECT * INTO current_primary
    FROM claimcore_witness.installations
    WHERE installation=p_installation FOR UPDATE;

    IF NOT FOUND OR current_primary.generation IS DISTINCT FROM p_generation THEN
        RETURN false;
    END IF;

    IF current_primary.transition IS NOT NULL THEN
        RETURN current_primary.transition IS NOT DISTINCT FROM p_transition;
    END IF;

    IF p_transition IS NULL OR p_transition='00000000-0000-0000-0000-000000000000'
        OR EXISTS (SELECT FROM claimcore_witness.installations WHERE generation=p_transition)
        OR EXISTS (SELECT FROM claimcore_witness.transitions WHERE prior_generation=p_transition) THEN
        RETURN false;
    END IF;

    INSERT INTO claimcore_witness.transitions(
        transition,installation,prior_generation,prior_primary_id,system_id,timeline,required_lsn,
        required_sequence,required_hash,required_expiry_ticks)
    VALUES (
        p_transition,p_installation,p_generation,current_primary.primary_id,
        current_primary.system_id,current_primary.timeline,current_primary.disclosure_lsn,
        current_primary.admitted_sequence,current_primary.admitted_hash,current_primary.expiry_floor_ticks)
    ON CONFLICT DO NOTHING;

    IF NOT FOUND THEN
        RETURN false;
    END IF;

    UPDATE claimcore_witness.installations SET transition=p_transition
    WHERE installation=p_installation;
    RETURN true;
END
$$;

CREATE FUNCTION claimcore_witness.supersede_lost_target(
    p_installation uuid,p_expected uuid,p_replacement uuid)
RETURNS boolean LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE
    pending uuid;
BEGIN
    IF current_setting('fsync')<>'on' THEN
        RETURN false;
    END IF;

    SELECT transition INTO pending FROM claimcore_witness.installations
    WHERE installation=p_installation FOR UPDATE;

    IF pending IS NULL OR p_expected IS NULL OR p_replacement IS NULL
        OR p_replacement='00000000-0000-0000-0000-000000000000'
        OR p_expected=p_replacement THEN
        RETURN false;
    END IF;

    IF pending=p_replacement THEN
        RETURN EXISTS (
            SELECT FROM claimcore_witness.transitions
            WHERE installation=p_installation AND transition=pending AND supersedes=p_expected);
    END IF;

    IF pending<>p_expected
        OR EXISTS (SELECT FROM claimcore_witness.transitions WHERE transition=p_replacement)
        OR EXISTS (SELECT FROM claimcore_witness.installations WHERE generation=p_replacement)
        OR EXISTS (SELECT FROM claimcore_witness.transitions WHERE prior_generation=p_replacement) THEN
        RETURN false;
    END IF;

    INSERT INTO claimcore_witness.transitions(transition,installation,supersedes,fence_origin,superseded_at)
    SELECT p_replacement,installation,transition,coalesce(fence_origin,transition),clock_timestamp()
    FROM claimcore_witness.transitions
    WHERE installation=p_installation AND transition=p_expected AND sealed_digest IS NOT NULL
    ON CONFLICT DO NOTHING;

    IF NOT FOUND THEN
        RETURN false;
    END IF;

    UPDATE claimcore_witness.installations SET transition=p_replacement
    WHERE installation=p_installation;
    RETURN true;
END
$$;

CREATE FUNCTION claimcore_witness.record_gap(p_installation uuid,p_transition uuid,p_reason text)
RETURNS boolean LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    -- Lock the pending installation first. Both decision commands then update the
    -- same transition row, which also protects against a stale transaction snapshot.
    PERFORM 1 FROM claimcore_witness.installations
    WHERE installation=p_installation AND transition=p_transition FOR UPDATE;
    IF NOT FOUND THEN
        RETURN false;
    END IF;

    IF p_reason IS NULL THEN
        RETURN false;
    END IF;

    UPDATE claimcore_witness.transitions SET gap_reason=p_reason,gap_recorded_at=clock_timestamp()
    WHERE installation=p_installation AND transition=p_transition
        AND required_lsn IS NOT NULL AND fence_origin IS NULL AND sealed_digest IS NULL AND gap_reason IS NULL;
    IF FOUND THEN
        RETURN true;
    END IF;

    -- An exact repeat of a recorded decision is an observation, even after sealing.
    RETURN EXISTS (
        SELECT FROM claimcore_witness.fence_decisions
        WHERE installation=p_installation AND transition=p_transition AND (decision).gap_reason=p_reason);
END
$$;

-- Seal the branch, proposed gap and admitted checkpoint together.
-- The dispatch floor has its own monotonic checks at activation.
CREATE FUNCTION claimcore_witness.fence_digest(p_installation uuid,p_transition uuid)
RETURNS bytea LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
    SELECT sha256(convert_to(jsonb_build_array(
        (decision).prior_generation,(decision).prior_primary_id,(decision).system_id,
        (decision).timeline,(decision).required_lsn::text,(decision).gap_reason,
        (decision).required_sequence,encode((decision).required_hash,'hex'),(decision).required_expiry_ticks
    )::text,'UTF8'))
    FROM claimcore_witness.fence_decisions t
    WHERE t.installation=p_installation AND t.transition=p_transition
$$;

-- The application authenticates the target and its history. The witness checks
-- that the supplied fork covers the retained position or a recorded gap applies.
CREATE FUNCTION claimcore_witness.covers_disclosures(
    claimcore_witness.transitions,bigint,text,pg_lsn,pg_lsn)
RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
    SELECT ($1).required_lsn IS NULL OR ($1).gap_reason IS NOT NULL
        OR (($1).system_id=$2 AND ($1).timeline<>$3
            AND ($1).required_lsn<=$5 AND $5<=$4)
$$;

CREATE FUNCTION claimcore_witness.seal_fence(
    p_installation uuid,p_transition uuid,p_expected bytea,
    p_system_id bigint,p_timeline text,p_flushed pg_lsn,p_fork pg_lsn,
    p_target uuid,p_approval bytea,p_checkpoint bytea)
RETURNS boolean LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    PERFORM 1 FROM claimcore_witness.installations
    WHERE installation=p_installation AND transition=p_transition FOR UPDATE;
    IF NOT FOUND OR p_expected IS NULL OR p_target IS NULL OR p_approval IS NULL OR p_checkpoint IS NULL
        OR p_target='00000000-0000-0000-0000-000000000000' THEN
        RETURN false;
    END IF;

    UPDATE claimcore_witness.transitions t
    SET sealed_digest=p_expected,sealed_at=clock_timestamp(),sealed_target=p_target,sealed_approval=p_approval,
        checkpoint_digest=CASE WHEN t.fence_origin IS NULL THEN p_checkpoint END
    FROM claimcore_witness.fence_decisions d
    WHERE t.installation=p_installation AND t.transition=p_transition
        AND d.installation=t.installation AND d.transition=t.transition
        AND t.sealed_digest IS NULL
        AND (t.fence_origin IS NULL OR (d.decision).checkpoint_digest=p_checkpoint)
        AND claimcore_witness.fence_digest(p_installation,p_transition)=p_expected
        AND claimcore_witness.covers_disclosures(d.decision,p_system_id,p_timeline,p_flushed,p_fork);
    IF FOUND THEN
        RETURN true;
    END IF;

    RETURN EXISTS (
        SELECT FROM claimcore_witness.transitions t
        JOIN claimcore_witness.fence_decisions d USING (installation,transition)
        WHERE t.installation=p_installation AND t.transition=p_transition
            AND t.sealed_digest=p_expected
            AND (d.decision).checkpoint_digest=p_checkpoint
            AND t.sealed_target=p_target AND t.sealed_approval=p_approval
            AND claimcore_witness.covers_disclosures(d.decision,p_system_id,p_timeline,p_flushed,p_fork));
END
$$;

CREATE TABLE claimcore_witness.preparations (
    installation uuid NOT NULL,
    transition uuid NOT NULL,
    primary_id uuid NOT NULL UNIQUE,
    system_id bigint NOT NULL,
    timeline text NOT NULL CHECK (timeline ~ '^[0-9A-F]{8}$'),
    fence_digest bytea NOT NULL CHECK (octet_length(fence_digest)=32),
    preparation_digest bytea NOT NULL CHECK (octet_length(preparation_digest)=32),
    admitted_sequence bigint NOT NULL CHECK (admitted_sequence>0),
    admitted_hash bytea NOT NULL CHECK (octet_length(admitted_hash)=32),
    expiry_floor_ticks bigint CHECK (expiry_floor_ticks BETWEEN 0 AND 3155378975999999999),
    credential_floor timestamptz NOT NULL CHECK (isfinite(credential_floor)),
    prepared_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (installation,transition),
    FOREIGN KEY (installation,transition)
        REFERENCES claimcore_witness.transitions(installation,transition)
);

-- Prefix membership at a higher head is verified by replay on the target.
-- The witness independently rejects a lower position or cutoff and a different hash at equality.
CREATE FUNCTION claimcore_witness.retains_checkpoint(bigint,bytea,bigint,bigint,bytea,bigint)
RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
    SELECT ($1>$4 OR ($1=$4 AND $2=$5)) AND ($6 IS NULL OR $3 >= $6)
$$;

CREATE FUNCTION claimcore_witness.prepare_target(uuid,uuid,uuid,bigint,text,bytea,bytea,bigint,bytea,bigint,timestamptz)
RETURNS boolean LANGUAGE sql SET search_path=pg_catalog AS $$
    WITH fenced AS MATERIALIZED (
        SELECT installation FROM claimcore_witness.installations
        WHERE installation=$1 AND transition=$2
            AND isfinite($11) AND (credential_floor IS NULL OR $11 >= credential_floor)
            AND claimcore_witness.retains_checkpoint(
                $8,$9,$10,admitted_sequence,admitted_hash,expiry_floor_ticks)
            AND EXISTS(SELECT FROM claimcore_witness.transitions t
                       WHERE t.installation=$1 AND t.transition=$2 AND t.sealed_target=$3 AND t.sealed_digest=$6)
        FOR UPDATE
    ), prepared AS (
        INSERT INTO claimcore_witness.preparations(
            installation,transition,primary_id,system_id,timeline,fence_digest,preparation_digest,
            admitted_sequence,admitted_hash,expiry_floor_ticks,credential_floor)
        SELECT installation,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11 FROM fenced
        ON CONFLICT (installation,transition) DO NOTHING
        RETURNING 1
    )
    SELECT EXISTS(SELECT FROM prepared) OR EXISTS (
        SELECT FROM fenced f JOIN claimcore_witness.preparations p USING (installation)
        WHERE p.transition=$2 AND p.primary_id=$3 AND p.system_id=$4 AND p.timeline=$5
            AND p.fence_digest=$6 AND p.preparation_digest=$7
            AND p.admitted_sequence=$8 AND p.admitted_hash=$9 AND p.expiry_floor_ticks IS NOT DISTINCT FROM $10
            AND p.credential_floor=$11
    )
$$;

CREATE TABLE claimcore_witness.activations (
    installation uuid NOT NULL,
    generation uuid NOT NULL,
    system_id bigint NOT NULL,
    timeline text NOT NULL,
    old_required_lsn pg_lsn,
    admitted_lsn pg_lsn NOT NULL,
    suppress_through date NOT NULL CHECK (isfinite(suppress_through)),
    activated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (installation,generation),
    FOREIGN KEY (installation,generation)
        REFERENCES claimcore_witness.preparations(installation,transition)
);

CREATE FUNCTION claimcore_witness.activate(uuid,uuid,bigint,text,pg_lsn,pg_lsn,date,uuid,bytea)
RETURNS boolean LANGUAGE sql SET search_path=pg_catalog AS $$
    WITH previous AS MATERIALIZED (
        SELECT * FROM claimcore_witness.installations
        WHERE installation=$1 AND transition=$2
            AND EXISTS(
                SELECT FROM claimcore_witness.preparations p
                JOIN claimcore_witness.transitions t USING (installation,transition)
                JOIN claimcore_witness.fence_decisions d USING (installation,transition)
                WHERE p.installation=$1 AND p.transition=$2
                    AND p.primary_id=$8 AND p.system_id=$3 AND p.timeline=$4
                    AND p.preparation_digest=$9 AND p.fence_digest=t.sealed_digest AND t.sealed_target=$8
                    AND claimcore_witness.retains_checkpoint(
                        p.admitted_sequence,p.admitted_hash,p.expiry_floor_ticks,
                        installations.admitted_sequence,installations.admitted_hash,installations.expiry_floor_ticks)
                    AND claimcore_witness.covers_disclosures(d.decision,$3,$4,$5,$6))
            AND $5>'0/0'::pg_lsn
            AND (latest_dispatch IS NULL OR $7 >= latest_dispatch)
            AND (suppress_through IS NULL OR $7 >= suppress_through)
        FOR UPDATE
    ), activation AS (
        INSERT INTO claimcore_witness.activations(installation,generation,system_id,timeline,old_required_lsn,admitted_lsn,suppress_through)
        SELECT installation,$2,$3,$4,disclosure_lsn,$5,$7 FROM previous
        RETURNING installation,generation
    ), published AS (
        UPDATE claimcore_witness.installations i
        SET generation=$2,primary_id=$8,system_id=$3,timeline=$4,disclosure_lsn=$5,transition=NULL,suppress_through=$7,
            admitted_sequence=p.admitted_sequence,admitted_hash=p.admitted_hash,expiry_floor_ticks=p.expiry_floor_ticks,
            credential_floor=p.credential_floor
        FROM activation a
        JOIN claimcore_witness.preparations p ON p.installation=a.installation AND p.transition=a.generation
        WHERE i.installation=a.installation
        RETURNING i.installation
    )
    SELECT EXISTS(SELECT FROM published)
        OR EXISTS (
            SELECT FROM claimcore_witness.installations
            WHERE installation=$1 AND generation=$2 AND system_id=$3 AND timeline=$4 AND primary_id=$8 AND transition IS NULL
                AND EXISTS(SELECT FROM claimcore_witness.preparations p WHERE p.installation=$1 AND p.transition=$2
                           AND p.primary_id=$8 AND p.preparation_digest=$9)
        )
$$;

CREATE FUNCTION claimcore_witness.widen_reopen_floor(uuid,uuid,uuid,bigint,text,date)
RETURNS boolean LANGUAGE sql SET search_path=pg_catalog AS $$
    WITH admitted AS (
        UPDATE claimcore_witness.installations
        SET suppress_through=greatest(suppress_through,latest_dispatch,$6)
        WHERE installation=$1 AND generation=$2 AND primary_id=$3 AND system_id=$4 AND timeline=$5
            AND transition IS NULL AND isfinite($6)
        RETURNING 1
    )
    SELECT EXISTS(SELECT FROM admitted)
$$;

REVOKE ALL ON ALL TABLES IN SCHEMA claimcore_witness FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA claimcore_witness FROM PUBLIC;
GRANT USAGE ON SCHEMA claimcore_witness TO claimcore_witness_runtime;
GRANT EXECUTE ON FUNCTION claimcore_witness.is_active(uuid,uuid,bigint,text,uuid,timestamptz) TO claimcore_witness_runtime;
GRANT EXECUTE ON FUNCTION claimcore_witness.acknowledge_disclosure(uuid,uuid,bigint,text,uuid,pg_lsn) TO claimcore_witness_runtime;
GRANT EXECUTE ON FUNCTION claimcore_witness.admit_dispatch(uuid,uuid,bigint,text,uuid,date) TO claimcore_witness_runtime;
COMMIT;
