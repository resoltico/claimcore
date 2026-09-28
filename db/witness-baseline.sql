-- Fresh, independent PostgreSQL witness installation.
-- The schema owner executes this script in one transaction after creating
-- claimcore_witness_writer and claimcore_witness_auditor as distinct NOINHERIT,
-- non-owner login roles. The auditor has no append or owner-function authority.
CREATE SCHEMA claimcore_witness;
REVOKE ALL ON SCHEMA claimcore_witness FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE TABLE claimcore_witness.installation (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    installation_id uuid NOT NULL,
    lineage_id uuid NOT NULL,
    initial_key_id uuid NOT NULL,
    active_key_id uuid NOT NULL,
    key_check_envelope bytea NOT NULL CHECK (octet_length(key_check_envelope) BETWEEN 32 AND 4096),
    epoch bigint NOT NULL CHECK (epoch > 0),
    writer_generation bigint NOT NULL DEFAULT 1 CHECK (writer_generation > 0),
    data_use_scope text NOT NULL CHECK (data_use_scope IN ('SYNTHETIC_ONLY','REAL_DATA')),
    data_use_phase text NOT NULL CHECK (data_use_phase IN ('BOOTSTRAP_NO_CASES','ACTIVE')),
    data_use_activation_event_id uuid,
    data_use_activation_intent_sequence bigint CHECK (
        data_use_activation_intent_sequence IS NULL OR data_use_activation_intent_sequence > 0
    ),
    data_use_activation_intent_hash bytea CHECK (
        data_use_activation_intent_hash IS NULL OR octet_length(data_use_activation_intent_hash) = 32
    ),
    data_use_activation_sequence bigint CHECK (
        data_use_activation_sequence IS NULL OR data_use_activation_sequence > 0
    ),
    data_use_activation_hash bytea CHECK (
        data_use_activation_hash IS NULL OR octet_length(data_use_activation_hash) = 32
    ),
    data_use_activation_canonical bytea CHECK (
        data_use_activation_canonical IS NULL OR octet_length(data_use_activation_canonical) BETWEEN 1 AND 8192
    ),
    writer_capability_sha256 bytea NOT NULL CHECK (octet_length(writer_capability_sha256) = 32),
    handoff_pending boolean NOT NULL DEFAULT false,
    activation_pending boolean NOT NULL DEFAULT false,
    activation_event_id uuid,
    activation_sequence bigint CHECK (activation_sequence IS NULL OR activation_sequence > 0),
    activation_hash bytea CHECK (activation_hash IS NULL OR octet_length(activation_hash) = 32),
    last_aborted_handoff_id uuid,
    last_aborted_handoff_sequence bigint CHECK (
        last_aborted_handoff_sequence IS NULL OR last_aborted_handoff_sequence > 0
    ),
    last_aborted_handoff_hash bytea CHECK (
        last_aborted_handoff_hash IS NULL OR octet_length(last_aborted_handoff_hash) = 32
    ),
    tip_sequence bigint NOT NULL DEFAULT 0 CHECK (tip_sequence >= 0),
    tip_hash bytea NOT NULL CHECK (octet_length(tip_hash) = 32),
    baseline_id text NOT NULL CHECK (baseline_id = 'claimcore-witness-v1'),
    baseline_sha256 text NOT NULL CHECK (baseline_sha256 ~ '^[0-9a-f]{64}$'),
    UNIQUE (installation_id, lineage_id),
    CONSTRAINT installation_data_use_shape CHECK (
        (data_use_scope='SYNTHETIC_ONLY' AND data_use_phase='ACTIVE'
            AND data_use_activation_event_id IS NULL AND data_use_activation_intent_sequence IS NULL
            AND data_use_activation_intent_hash IS NULL AND data_use_activation_sequence IS NULL
            AND data_use_activation_hash IS NULL AND data_use_activation_canonical IS NULL)
        OR (data_use_scope='REAL_DATA' AND
            ((data_use_phase='BOOTSTRAP_NO_CASES'
                AND data_use_activation_event_id IS NULL AND data_use_activation_intent_sequence IS NULL
                AND data_use_activation_intent_hash IS NULL AND data_use_activation_sequence IS NULL
                AND data_use_activation_hash IS NULL AND data_use_activation_canonical IS NULL)
             OR (data_use_phase='ACTIVE' AND data_use_activation_event_id IS NOT NULL
                AND data_use_activation_intent_sequence IS NOT NULL
                AND data_use_activation_intent_hash IS NOT NULL
                AND data_use_activation_sequence = data_use_activation_intent_sequence + 1
                AND data_use_activation_hash IS NOT NULL AND data_use_activation_canonical IS NOT NULL)))
    ),
    CONSTRAINT installation_abort_ticket_shape CHECK (
        (last_aborted_handoff_id IS NULL AND last_aborted_handoff_sequence IS NULL
            AND last_aborted_handoff_hash IS NULL)
        OR (last_aborted_handoff_id IS NOT NULL AND last_aborted_handoff_sequence IS NOT NULL
            AND last_aborted_handoff_hash IS NOT NULL)
    ),
    CONSTRAINT installation_activation_shape CHECK (
        (writer_generation = 1 AND NOT activation_pending AND activation_event_id IS NULL
            AND activation_sequence IS NULL AND activation_hash IS NULL)
        OR (writer_generation > 1 AND
            ((activation_pending AND activation_event_id IS NULL
                AND activation_sequence IS NULL AND activation_hash IS NULL)
             OR (NOT activation_pending AND activation_event_id IS NOT NULL
                AND activation_sequence IS NOT NULL AND activation_hash IS NOT NULL)))
    )
);

CREATE TABLE claimcore_witness.journal (
    installation_id uuid NOT NULL,
    lineage_id uuid NOT NULL,
    epoch bigint NOT NULL CHECK (epoch > 0),
    sequence bigint NOT NULL CHECK (sequence > 0),
    operation_id uuid NOT NULL,
    scope_kind text NOT NULL CHECK (scope_kind IN ('INSTALLATION', 'CASE')),
    subject_case_id uuid,
    CONSTRAINT journal_scope_subject_check CHECK (
        (scope_kind = 'CASE' AND subject_case_id IS NOT NULL
            AND subject_case_id <> '00000000-0000-0000-0000-000000000000'::uuid)
        OR (scope_kind = 'INSTALLATION' AND subject_case_id IS NULL)
    ),
    key_id uuid NOT NULL,
    phase text NOT NULL CHECK (phase IN (
        'INTENT', 'SETTLED_ACCEPTED', 'SETTLED_REVOKED',
        'SETTLED_AUTHORITY', 'ABORTED_BEFORE_COMMIT', 'KEY_ROTATED'
    )),
    payload_sha256 bytea NOT NULL CHECK (octet_length(payload_sha256) = 32),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    entry_hash bytea NOT NULL CHECK (octet_length(entry_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (installation_id, sequence),
    UNIQUE (installation_id, operation_id, phase)
);

-- Hash-linked metadata remains immutable after an authorized privacy purge. Ciphertext is
-- separately owner-prunable; the writer cannot remove or rewrite either relation.
CREATE TABLE claimcore_witness.journal_payloads (
    installation_id uuid NOT NULL,
    sequence bigint NOT NULL,
    subject_case_id uuid,
    encrypted_payload bytea NOT NULL CHECK (octet_length(encrypted_payload) BETWEEN 1 AND 1048576),
    PRIMARY KEY (installation_id, sequence),
    FOREIGN KEY (installation_id, sequence)
        REFERENCES claimcore_witness.journal (installation_id, sequence)
);
CREATE INDEX journal_payloads_by_case
    ON claimcore_witness.journal_payloads (subject_case_id, installation_id, sequence)
    WHERE subject_case_id IS NOT NULL;

-- Owner-only handoff evidence is not writable by the runtime role. The pending fence is
-- co-committed with its journal INTENT; the new writer remains closed until settlement.
CREATE TABLE claimcore_witness.writer_handoffs (
    handoff_id uuid PRIMARY KEY,
    old_generation bigint NOT NULL CHECK (old_generation > 0),
    new_generation bigint NOT NULL CHECK (new_generation = old_generation + 1),
    previous_sequence bigint NOT NULL CHECK (previous_sequence >= 0),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    new_capability_sha256 bytea NOT NULL CHECK (octet_length(new_capability_sha256) = 32),
    checkpoint_signing_key_id uuid NOT NULL,
    prepare_canonical bytea NOT NULL CHECK (octet_length(prepare_canonical) BETWEEN 1 AND 16384),
    prepare_signature bytea NOT NULL CHECK (octet_length(prepare_signature) = 64),
    approval_one_id uuid NOT NULL,
    approval_two_id uuid NOT NULL,
    CONSTRAINT writer_handoff_distinct_approvals CHECK (approval_one_id <> approval_two_id),
    prepare_candidate_sha256 bytea NOT NULL CHECK (octet_length(prepare_candidate_sha256) = 32),
    prepare_sequence bigint NOT NULL UNIQUE CHECK (prepare_sequence > 0),
    prepare_hash bytea NOT NULL CHECK (octet_length(prepare_hash) = 32),
    settlement_candidate_sha256 bytea CHECK (
        settlement_candidate_sha256 IS NULL OR octet_length(settlement_candidate_sha256) = 32
    ),
    settlement_sequence bigint UNIQUE CHECK (settlement_sequence IS NULL OR settlement_sequence > 0),
    settlement_hash bytea CHECK (settlement_hash IS NULL OR octet_length(settlement_hash) = 32),
    settlement_canonical bytea CHECK (
        settlement_canonical IS NULL OR octet_length(settlement_canonical) BETWEEN 1 AND 16384
    ),
    settlement_signature bytea CHECK (
        settlement_signature IS NULL OR octet_length(settlement_signature) = 64
    ),
    activation_id uuid UNIQUE,
    activation_candidate_sha256 bytea CHECK (
        activation_candidate_sha256 IS NULL OR octet_length(activation_candidate_sha256) = 32
    ),
    activation_intent_sequence bigint UNIQUE CHECK (
        activation_intent_sequence IS NULL OR activation_intent_sequence > 0
    ),
    activation_intent_hash bytea CHECK (
        activation_intent_hash IS NULL OR octet_length(activation_intent_hash) = 32
    ),
    activation_sequence bigint UNIQUE CHECK (activation_sequence IS NULL OR activation_sequence > 0),
    activation_hash bytea CHECK (activation_hash IS NULL OR octet_length(activation_hash) = 32),
    activation_canonical bytea CHECK (
        activation_canonical IS NULL OR octet_length(activation_canonical) BETWEEN 1 AND 16384
    ),
    abort_candidate_sha256 bytea CHECK (
        abort_candidate_sha256 IS NULL OR octet_length(abort_candidate_sha256) = 32
    ),
    abort_sequence bigint UNIQUE CHECK (abort_sequence IS NULL OR abort_sequence > prepare_sequence),
    abort_hash bytea CHECK (abort_hash IS NULL OR octet_length(abort_hash) = 32),
    abort_canonical bytea CHECK (
        abort_canonical IS NULL OR octet_length(abort_canonical) BETWEEN 1 AND 16384
    ),
    abort_signature_one bytea CHECK (
        abort_signature_one IS NULL OR octet_length(abort_signature_one) = 64
    ),
    abort_signature_two bytea CHECK (
        abort_signature_two IS NULL OR octet_length(abort_signature_two) = 64
    ),
    abort_signing_key_one uuid,
    abort_signing_key_two uuid,
    CONSTRAINT writer_handoff_abort_distinct_keys CHECK (
        abort_signing_key_one IS NULL OR abort_signing_key_two IS NULL
        OR abort_signing_key_one <> abort_signing_key_two
    ),
    CONSTRAINT writer_handoff_settlement_shape CHECK (
        (settlement_candidate_sha256 IS NULL AND settlement_sequence IS NULL
            AND settlement_hash IS NULL AND settlement_canonical IS NULL
            AND settlement_signature IS NULL)
        OR (settlement_candidate_sha256 IS NOT NULL AND settlement_sequence IS NOT NULL
            AND settlement_hash IS NOT NULL AND settlement_canonical IS NOT NULL
            AND settlement_signature IS NOT NULL)
    ),
    CONSTRAINT writer_handoff_activation_shape CHECK (
        (activation_id IS NULL AND activation_candidate_sha256 IS NULL
            AND activation_intent_sequence IS NULL AND activation_intent_hash IS NULL
            AND activation_sequence IS NULL AND activation_hash IS NULL
            AND activation_canonical IS NULL)
        OR (activation_id IS NOT NULL AND activation_candidate_sha256 IS NOT NULL
            AND activation_intent_sequence IS NOT NULL AND activation_intent_hash IS NOT NULL
            AND activation_sequence = activation_intent_sequence + 1
            AND activation_hash IS NOT NULL
            AND activation_canonical IS NOT NULL AND settlement_sequence IS NOT NULL)
    ),
    CONSTRAINT writer_handoff_abort_shape CHECK (
        (abort_candidate_sha256 IS NULL AND abort_sequence IS NULL AND abort_hash IS NULL
            AND abort_canonical IS NULL AND abort_signature_one IS NULL
            AND abort_signature_two IS NULL AND abort_signing_key_one IS NULL
            AND abort_signing_key_two IS NULL)
        OR (abort_candidate_sha256 IS NOT NULL AND abort_sequence IS NOT NULL
            AND abort_hash IS NOT NULL AND abort_canonical IS NOT NULL
            AND abort_signature_one IS NOT NULL AND abort_signature_two IS NOT NULL
            AND abort_signing_key_one IS NOT NULL AND abort_signing_key_two IS NOT NULL
            AND settlement_sequence IS NULL)
    )
);

-- A read-only writer can hold a row-share lock only through this narrow definer function.
-- The caller's transaction retains the lock until its claimant-bearing core read completes.
CREATE FUNCTION claimcore_witness.acquire_read_fence(
    p_installation_id uuid,
    p_lineage_id uuid,
    p_epoch bigint,
    p_writer_capability bytea
) RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $read_fence$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
BEGIN
    IF session_user <> 'claimcore_witness_writer'
       OR p_writer_capability IS NULL OR octet_length(p_writer_capability) <> 32 THEN
        RAISE EXCEPTION 'writer read fence is unavailable';
    END IF;
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR SHARE;
    IF v_installation.installation_id <> p_installation_id
       OR v_installation.lineage_id <> p_lineage_id
       OR v_installation.epoch <> p_epoch
       OR v_installation.handoff_pending
       OR v_installation.activation_pending
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256(p_writer_capability) THEN
        RAISE EXCEPTION 'writer read fence is unavailable';
    END IF;
    RETURN v_installation.writer_generation;
END
$read_fence$;

CREATE FUNCTION claimcore_witness.append(
    p_installation_id uuid,
    p_lineage_id uuid,
    p_epoch bigint,
    p_operation_id uuid,
    p_scope_kind text,
    p_subject_case_id uuid,
    p_phase text,
    p_key_id uuid,
    p_encrypted_payload bytea,
    p_writer_capability bytea
) RETURNS TABLE(sequence bigint, entry_hash bytea, payload_sha256 bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $body$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_existing claimcore_witness.journal%ROWTYPE;
    v_digest bytea;
    v_sequence bigint;
    v_hash bytea;
    v_scope_kind text;
    v_subject_case_id uuid;
BEGIN
    IF p_installation_id IS NULL OR p_lineage_id IS NULL OR p_epoch IS NULL
       OR p_operation_id IS NULL OR p_phase IS NULL OR p_key_id IS NULL
       OR p_encrypted_payload IS NULL
       OR octet_length(p_encrypted_payload) NOT BETWEEN 1 AND 1048576
       OR p_writer_capability IS NULL OR octet_length(p_writer_capability) <> 32
       OR p_phase NOT IN ('INTENT', 'SETTLED_ACCEPTED', 'SETTLED_REVOKED',
                         'SETTLED_AUTHORITY', 'ABORTED_BEFORE_COMMIT', 'KEY_ROTATED')
    THEN
        RAISE EXCEPTION 'invalid witness append';
    END IF;
    IF p_phase = 'KEY_ROTATED' AND session_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'witness key rotation requires schema owner';
    END IF;
    IF p_subject_case_id = '00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness subject';
    END IF;
    IF p_phase IN ('INTENT', 'KEY_ROTATED') THEN
        IF NOT (
            (p_scope_kind = 'CASE' AND p_subject_case_id IS NOT NULL)
            OR (p_scope_kind = 'INSTALLATION' AND p_subject_case_id IS NULL)
        ) OR (p_phase = 'KEY_ROTATED' AND p_scope_kind <> 'INSTALLATION') THEN
            RAISE EXCEPTION 'invalid witness scope';
        END IF;
    ELSIF p_scope_kind IS NOT NULL OR p_subject_case_id IS NOT NULL THEN
        RAISE EXCEPTION 'witness settlement scope must inherit intent';
    END IF;

    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    IF v_installation.installation_id <> p_installation_id
       OR v_installation.lineage_id <> p_lineage_id
       OR v_installation.epoch <> p_epoch
       OR v_installation.active_key_id <> p_key_id THEN
        RAISE EXCEPTION 'witness identity or epoch mismatch';
    END IF;
    IF v_installation.handoff_pending OR v_installation.activation_pending
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256(p_writer_capability) THEN
        RAISE EXCEPTION 'witness writer generation is fenced';
    END IF;

    v_scope_kind := p_scope_kind;
    v_subject_case_id := p_subject_case_id;
    IF p_phase NOT IN ('INTENT', 'KEY_ROTATED') THEN
        SELECT scope_kind, subject_case_id INTO v_scope_kind, v_subject_case_id
          FROM claimcore_witness.journal
          WHERE installation_id=p_installation_id
            AND operation_id=p_operation_id AND phase='INTENT';
        IF NOT FOUND THEN
            RAISE EXCEPTION 'witness settlement has no intent';
        END IF;
    END IF;

    v_digest := pg_catalog.sha256(p_encrypted_payload);
    SELECT * INTO v_existing FROM claimcore_witness.journal
      WHERE installation_id = p_installation_id
        AND operation_id = p_operation_id AND phase = p_phase;
    IF FOUND THEN
        IF v_existing.lineage_id <> p_lineage_id OR v_existing.epoch <> p_epoch
           OR v_existing.payload_sha256 <> v_digest
           OR v_existing.key_id <> p_key_id
           OR v_existing.scope_kind <> v_scope_kind
           OR v_existing.subject_case_id IS DISTINCT FROM v_subject_case_id
           OR NOT EXISTS (
               SELECT 1 FROM claimcore_witness.journal_payloads p
               WHERE p.installation_id=v_existing.installation_id
                 AND p.sequence=v_existing.sequence
                 AND p.subject_case_id IS NOT DISTINCT FROM v_subject_case_id
                 AND p.encrypted_payload=p_encrypted_payload
           ) THEN
            RAISE EXCEPTION 'divergent witness retry';
        END IF;
        sequence := v_existing.sequence;
        entry_hash := v_existing.entry_hash;
        payload_sha256 := v_existing.payload_sha256;
        RETURN NEXT;
        RETURN;
    END IF;

    IF p_phase NOT IN ('INTENT', 'KEY_ROTATED') THEN
        IF NOT EXISTS (
            SELECT 1 FROM claimcore_witness.journal
            WHERE installation_id = p_installation_id
              AND operation_id = p_operation_id AND phase = 'INTENT'
        ) OR EXISTS (
            SELECT 1 FROM claimcore_witness.journal
            WHERE installation_id = p_installation_id
              AND operation_id = p_operation_id AND phase <> 'INTENT'
        ) THEN
            RAISE EXCEPTION 'witness settlement requires one unsettled intent';
        END IF;
    END IF;

    v_sequence := v_installation.tip_sequence + 1;
    v_hash := pg_catalog.sha256(
        v_installation.tip_hash || pg_catalog.convert_to(
            p_installation_id::text || ':' || p_lineage_id::text || ':' ||
            p_epoch::text || ':' || v_sequence::text || ':' ||
            p_operation_id::text || ':' || p_phase || ':' || p_key_id::text || ':' ||
            v_scope_kind || ':' ||
            COALESCE(v_subject_case_id::text, '-'),
            'UTF8') || v_digest);
    INSERT INTO claimcore_witness.journal
      (installation_id, lineage_id, epoch, sequence, operation_id, scope_kind, subject_case_id,
       phase, key_id, payload_sha256, previous_hash, entry_hash)
    VALUES
      (p_installation_id, p_lineage_id, p_epoch, v_sequence, p_operation_id,
       v_scope_kind, v_subject_case_id, p_phase, p_key_id,
       v_digest, v_installation.tip_hash, v_hash);
    INSERT INTO claimcore_witness.journal_payloads
      (installation_id, sequence, subject_case_id, encrypted_payload)
    VALUES (p_installation_id, v_sequence, v_subject_case_id, p_encrypted_payload);
    UPDATE claimcore_witness.installation
       SET tip_sequence = v_sequence, tip_hash = v_hash WHERE singleton;
    sequence := v_sequence;
    entry_hash := v_hash;
    payload_sha256 := v_digest;
    RETURN NEXT;
END
$body$;

-- The schema owner first validates the exact signed checkpoint, independent approvals,
-- current-pair audit and external fence. This function atomically records its witnessed
-- intent and closes ordinary writes; it cannot be called by the runtime role.
CREATE FUNCTION claimcore_witness.prepare_writer_handoff(
    p_installation_id uuid,
    p_lineage_id uuid,
    p_epoch bigint,
    p_handoff_id uuid,
    p_old_generation bigint,
    p_expected_sequence bigint,
    p_expected_hash bytea,
    p_new_capability_sha256 bytea,
    p_checkpoint_signing_key_id uuid,
    p_prepare_canonical bytea,
    p_prepare_signature bytea,
    p_approval_one_id uuid,
    p_approval_two_id uuid,
    p_key_id uuid,
    p_encrypted_candidate bytea,
    p_old_writer_capability bytea
) RETURNS TABLE(sequence bigint, entry_hash bytea, payload_sha256 bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $handoff_prepare$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_existing claimcore_witness.writer_handoffs%ROWTYPE;
    v_sequence bigint;
    v_hash bytea;
    v_payload_hash bytea;
BEGIN
    IF session_user <> 'claimcore_witness_owner'
       OR p_handoff_id IS NULL OR p_handoff_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_old_generation IS NULL OR p_old_generation < 1
       OR p_expected_sequence IS NULL OR p_expected_sequence < 0
       OR p_expected_hash IS NULL OR octet_length(p_expected_hash) <> 32
       OR p_new_capability_sha256 IS NULL OR octet_length(p_new_capability_sha256) <> 32
       OR p_checkpoint_signing_key_id IS NULL
       OR p_prepare_canonical IS NULL OR octet_length(p_prepare_canonical) NOT BETWEEN 1 AND 16384
       OR p_prepare_signature IS NULL OR octet_length(p_prepare_signature) <> 64
       OR p_approval_one_id IS NULL OR p_approval_two_id IS NULL
       OR p_approval_one_id = p_approval_two_id
       OR p_key_id IS NULL OR p_encrypted_candidate IS NULL
       OR octet_length(p_encrypted_candidate) NOT BETWEEN 1 AND 1048576
       OR p_old_writer_capability IS NULL OR octet_length(p_old_writer_capability) <> 32
    THEN
        RAISE EXCEPTION 'invalid writer handoff preparation';
    END IF;
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    SELECT * INTO v_existing FROM claimcore_witness.writer_handoffs h
      WHERE h.handoff_id=p_handoff_id;
    IF FOUND THEN
        IF NOT v_installation.handoff_pending
           OR v_existing.old_generation <> p_old_generation
           OR v_existing.prepare_canonical <> p_prepare_canonical
           OR v_existing.prepare_signature <> p_prepare_signature
           OR v_existing.previous_sequence <> p_expected_sequence
           OR v_existing.previous_hash <> p_expected_hash
           OR v_existing.new_capability_sha256 <> p_new_capability_sha256
           OR v_existing.settlement_sequence IS NOT NULL
           OR v_existing.abort_sequence IS NOT NULL THEN
            RAISE EXCEPTION 'divergent writer handoff preparation';
        END IF;
        sequence := v_existing.prepare_sequence;
        entry_hash := v_existing.prepare_hash;
        SELECT j.payload_sha256 INTO payload_sha256 FROM claimcore_witness.journal j
          WHERE j.installation_id=p_installation_id AND j.sequence=sequence;
        RETURN NEXT;
        RETURN;
    END IF;
    IF v_installation.installation_id <> p_installation_id
       OR v_installation.lineage_id <> p_lineage_id OR v_installation.epoch <> p_epoch
       OR v_installation.writer_generation <> p_old_generation
       OR v_installation.handoff_pending OR v_installation.activation_pending
       OR v_installation.tip_sequence <> p_expected_sequence
       OR v_installation.tip_hash <> p_expected_hash
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256(p_old_writer_capability)
       OR v_installation.writer_capability_sha256 = p_new_capability_sha256
       OR EXISTS (
           SELECT 1 FROM claimcore_witness.journal i
           WHERE i.installation_id=p_installation_id AND i.phase='INTENT'
             AND NOT EXISTS (
                 SELECT 1 FROM claimcore_witness.journal s
                 WHERE s.installation_id=i.installation_id
                   AND s.operation_id=i.operation_id
                   AND s.phase IN ('SETTLED_ACCEPTED','SETTLED_REVOKED',
                                   'SETTLED_AUTHORITY','ABORTED_BEFORE_COMMIT')
             )
       ) THEN
        RAISE EXCEPTION 'writer handoff prerequisites diverged';
    END IF;
    SELECT appended.sequence, appended.entry_hash, appended.payload_sha256
      INTO v_sequence, v_hash, v_payload_hash
      FROM claimcore_witness.append(
        p_installation_id,p_lineage_id,p_epoch,p_handoff_id,'INSTALLATION',NULL,
        'INTENT',p_key_id,p_encrypted_candidate,p_old_writer_capability) AS appended;
    INSERT INTO claimcore_witness.writer_handoffs
      (handoff_id,old_generation,new_generation,previous_sequence,previous_hash,
       new_capability_sha256,checkpoint_signing_key_id,prepare_canonical,
       prepare_signature,approval_one_id,approval_two_id,prepare_candidate_sha256,
       prepare_sequence,prepare_hash)
    VALUES
      (p_handoff_id,p_old_generation,p_old_generation+1,p_expected_sequence,p_expected_hash,
       p_new_capability_sha256,p_checkpoint_signing_key_id,p_prepare_canonical,
       p_prepare_signature,p_approval_one_id,p_approval_two_id,
       pg_catalog.sha256(p_prepare_canonical),v_sequence,v_hash);
    UPDATE claimcore_witness.installation SET handoff_pending=true WHERE singleton;
    sequence := v_sequence;
    entry_hash := v_hash;
    payload_sha256 := v_payload_hash;
    RETURN NEXT;
END
$handoff_prepare$;

-- Only the exact prepared handoff can clear the pending fence. The settlement journal row,
-- writer generation, new capability hash and tip advance commit in one witness transaction.
CREATE FUNCTION claimcore_witness.commit_writer_handoff(
    p_installation_id uuid,
    p_lineage_id uuid,
    p_epoch bigint,
    p_handoff_id uuid,
    p_prepare_sequence bigint,
    p_prepare_hash bytea,
    p_old_writer_capability bytea,
    p_new_writer_capability bytea,
    p_settlement_canonical bytea,
    p_settlement_signature bytea,
    p_key_id uuid,
    p_encrypted_settlement bytea
) RETURNS TABLE(sequence bigint, entry_hash bytea, payload_sha256 bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $handoff_commit$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_handoff claimcore_witness.writer_handoffs%ROWTYPE;
    v_digest bytea;
    v_sequence bigint;
    v_hash bytea;
BEGIN
    IF session_user <> 'claimcore_witness_owner'
       OR p_handoff_id IS NULL OR p_prepare_sequence IS NULL OR p_prepare_sequence < 1
       OR p_prepare_hash IS NULL OR octet_length(p_prepare_hash) <> 32
       OR p_old_writer_capability IS NULL OR octet_length(p_old_writer_capability) <> 32
       OR p_new_writer_capability IS NULL OR octet_length(p_new_writer_capability) <> 32
       OR p_settlement_canonical IS NULL
       OR octet_length(p_settlement_canonical) NOT BETWEEN 1 AND 16384
       OR p_settlement_signature IS NULL OR octet_length(p_settlement_signature) <> 64
       OR p_key_id IS NULL OR p_encrypted_settlement IS NULL
       OR octet_length(p_encrypted_settlement) NOT BETWEEN 1 AND 1048576
    THEN
        RAISE EXCEPTION 'invalid writer handoff settlement';
    END IF;
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    SELECT * INTO STRICT v_handoff FROM claimcore_witness.writer_handoffs h
      WHERE h.handoff_id=p_handoff_id FOR UPDATE;
    v_digest := pg_catalog.sha256(p_encrypted_settlement);
    IF v_handoff.prepare_sequence <> p_prepare_sequence
       OR v_handoff.prepare_hash <> p_prepare_hash
       OR v_handoff.new_capability_sha256 <> pg_catalog.sha256(p_new_writer_capability)
       OR v_handoff.abort_sequence IS NOT NULL
       OR v_installation.installation_id <> p_installation_id
       OR v_installation.lineage_id <> p_lineage_id OR v_installation.epoch <> p_epoch
       OR v_installation.active_key_id <> p_key_id
       OR NOT EXISTS (
           SELECT 1 FROM claimcore_witness.journal i
           WHERE i.installation_id=p_installation_id AND i.operation_id=p_handoff_id
             AND i.phase='INTENT' AND i.sequence=p_prepare_sequence
             AND i.entry_hash=p_prepare_hash AND i.scope_kind='INSTALLATION'
       ) THEN
        RAISE EXCEPTION 'writer handoff intent diverged';
    END IF;
    IF v_handoff.settlement_sequence IS NOT NULL THEN
        IF v_installation.handoff_pending
           OR v_installation.writer_generation <> v_handoff.new_generation
           OR v_installation.writer_capability_sha256 <> v_handoff.new_capability_sha256
           OR v_handoff.settlement_canonical <> p_settlement_canonical
           OR v_handoff.settlement_signature <> p_settlement_signature
           OR NOT EXISTS (
               SELECT 1 FROM claimcore_witness.journal j
               JOIN claimcore_witness.journal_payloads p
                 ON p.installation_id=j.installation_id AND p.sequence=j.sequence
               WHERE j.installation_id=p_installation_id AND j.operation_id=p_handoff_id
                 AND j.phase='SETTLED_AUTHORITY'
                 AND j.sequence=v_handoff.settlement_sequence
                 AND j.entry_hash=v_handoff.settlement_hash
                 AND j.payload_sha256=v_digest
                 AND p.encrypted_payload=p_encrypted_settlement
           ) THEN
            RAISE EXCEPTION 'divergent writer handoff retry';
        END IF;
        sequence := v_handoff.settlement_sequence;
        entry_hash := v_handoff.settlement_hash;
        payload_sha256 := v_digest;
        RETURN NEXT;
        RETURN;
    END IF;
    IF NOT v_installation.handoff_pending
       OR v_installation.writer_generation <> v_handoff.old_generation
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256(p_old_writer_capability)
       OR v_installation.tip_sequence <> p_prepare_sequence
       OR v_installation.tip_hash <> p_prepare_hash
       OR EXISTS (
           SELECT 1 FROM claimcore_witness.journal i
           WHERE i.installation_id=p_installation_id AND i.phase='INTENT'
             AND i.operation_id<>p_handoff_id
             AND NOT EXISTS (
                 SELECT 1 FROM claimcore_witness.journal s
                 WHERE s.installation_id=i.installation_id
                   AND s.operation_id=i.operation_id
                   AND s.phase IN ('SETTLED_ACCEPTED','SETTLED_REVOKED',
                                   'SETTLED_AUTHORITY','ABORTED_BEFORE_COMMIT')
             )
       ) THEN
        RAISE EXCEPTION 'writer handoff settlement prerequisites diverged';
    END IF;
    v_sequence := p_prepare_sequence + 1;
    v_hash := pg_catalog.sha256(
        p_prepare_hash || pg_catalog.convert_to(
            p_installation_id::text || ':' || p_lineage_id::text || ':' ||
            p_epoch::text || ':' || v_sequence::text || ':' ||
            p_handoff_id::text || ':SETTLED_AUTHORITY:' || p_key_id::text ||
            ':INSTALLATION:-', 'UTF8') || v_digest);
    INSERT INTO claimcore_witness.journal
      (installation_id,lineage_id,epoch,sequence,operation_id,scope_kind,
       subject_case_id,phase,key_id,payload_sha256,previous_hash,entry_hash)
    VALUES
      (p_installation_id,p_lineage_id,p_epoch,v_sequence,p_handoff_id,'INSTALLATION',
       NULL,'SETTLED_AUTHORITY',p_key_id,v_digest,p_prepare_hash,v_hash);
    INSERT INTO claimcore_witness.journal_payloads
      (installation_id,sequence,subject_case_id,encrypted_payload)
    VALUES (p_installation_id,v_sequence,NULL,p_encrypted_settlement);
    UPDATE claimcore_witness.writer_handoffs
       SET settlement_candidate_sha256=pg_catalog.sha256(p_settlement_canonical),
           settlement_sequence=v_sequence,settlement_hash=v_hash,
           settlement_canonical=p_settlement_canonical,
           settlement_signature=p_settlement_signature
      WHERE handoff_id=p_handoff_id;
    UPDATE claimcore_witness.installation
       SET writer_generation=v_handoff.new_generation,
           writer_capability_sha256=v_handoff.new_capability_sha256,
           handoff_pending=false,activation_pending=true,
           activation_event_id=NULL,activation_sequence=NULL,activation_hash=NULL,
           tip_sequence=v_sequence,tip_hash=v_hash
     WHERE singleton;
    sequence := v_sequence;
    entry_hash := v_hash;
    payload_sha256 := v_digest;
    RETURN NEXT;
END
$handoff_commit$;

-- Owner-only activation is a second authority act after W1 SETTLE. Its INTENT and
-- settlement append atomically while the restored writer remains quarantined. The
-- primary activation ticket is committed separately; either missing side stays closed.
CREATE FUNCTION claimcore_witness.activate_writer_handoff(
    p_installation_id uuid,
    p_lineage_id uuid,
    p_epoch bigint,
    p_handoff_id uuid,
    p_activation_id uuid,
    p_w1_sequence bigint,
    p_w1_hash bytea,
    p_canonical bytea,
    p_key_id uuid,
    p_encrypted_candidate bytea,
    p_encrypted_settlement bytea
) RETURNS TABLE(intent_sequence bigint, intent_hash bytea,
                settlement_sequence bigint, settlement_hash bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $writer_activation$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_handoff claimcore_witness.writer_handoffs%ROWTYPE;
    v_intent_sequence bigint;
    v_intent_hash bytea;
    v_settlement_sequence bigint;
    v_settlement_hash bytea;
    v_digest bytea;
BEGIN
    IF session_user <> 'claimcore_witness_owner'
       OR p_handoff_id IS NULL OR p_activation_id IS NULL
       OR p_activation_id = p_handoff_id
       OR p_w1_sequence IS NULL OR p_w1_sequence < 1
       OR p_w1_hash IS NULL OR octet_length(p_w1_hash) <> 32
       OR p_canonical IS NULL OR octet_length(p_canonical) NOT BETWEEN 1 AND 16384
       OR p_key_id IS NULL OR p_encrypted_candidate IS NULL
       OR octet_length(p_encrypted_candidate) NOT BETWEEN 1 AND 1048576
       OR p_encrypted_settlement IS NULL
       OR octet_length(p_encrypted_settlement) NOT BETWEEN 1 AND 1048576
    THEN
        RAISE EXCEPTION 'invalid writer activation';
    END IF;
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    SELECT * INTO STRICT v_handoff FROM claimcore_witness.writer_handoffs h
      WHERE h.handoff_id=p_handoff_id FOR UPDATE;
    IF v_installation.installation_id <> p_installation_id
       OR v_installation.lineage_id <> p_lineage_id
       OR v_installation.epoch <> p_epoch
       OR v_installation.active_key_id <> p_key_id
       OR v_handoff.settlement_sequence <> p_w1_sequence
       OR v_handoff.settlement_hash <> p_w1_hash
       OR v_handoff.abort_sequence IS NOT NULL
       OR v_installation.writer_generation <> v_handoff.new_generation
       OR v_installation.handoff_pending THEN
        RAISE EXCEPTION 'writer activation W1 evidence diverged';
    END IF;
    IF v_handoff.activation_id IS NOT NULL THEN
        IF v_handoff.activation_id <> p_activation_id
           OR v_handoff.activation_canonical <> p_canonical
           OR v_handoff.activation_candidate_sha256 <> pg_catalog.sha256(p_canonical)
           OR v_installation.activation_pending
           OR v_installation.activation_event_id <> p_activation_id
           OR v_installation.activation_sequence <> v_handoff.activation_sequence
           OR v_installation.activation_hash <> v_handoff.activation_hash
           OR NOT EXISTS (
               SELECT 1 FROM claimcore_witness.journal i
               JOIN claimcore_witness.journal s
                 ON s.installation_id=i.installation_id
                AND s.operation_id=i.operation_id AND s.phase='SETTLED_AUTHORITY'
               WHERE i.installation_id=p_installation_id
                 AND i.operation_id=p_activation_id AND i.phase='INTENT'
                 AND i.sequence=v_handoff.activation_intent_sequence
                 AND i.entry_hash=v_handoff.activation_intent_hash
                 AND s.sequence=v_handoff.activation_sequence
                 AND s.entry_hash=v_handoff.activation_hash
           ) THEN
            RAISE EXCEPTION 'divergent writer activation retry';
        END IF;
        intent_sequence := v_handoff.activation_intent_sequence;
        intent_hash := v_handoff.activation_intent_hash;
        settlement_sequence := v_handoff.activation_sequence;
        settlement_hash := v_handoff.activation_hash;
        RETURN NEXT;
        RETURN;
    END IF;
    IF NOT v_installation.activation_pending
       OR v_installation.activation_event_id IS NOT NULL
       OR v_installation.tip_sequence <> p_w1_sequence
       OR v_installation.tip_hash <> p_w1_hash
       OR EXISTS (
           SELECT 1 FROM claimcore_witness.journal i
           WHERE i.installation_id=p_installation_id AND i.phase='INTENT'
             AND NOT EXISTS (
                 SELECT 1 FROM claimcore_witness.journal s
                 WHERE s.installation_id=i.installation_id
                   AND s.operation_id=i.operation_id
                   AND s.phase IN ('SETTLED_ACCEPTED','SETTLED_REVOKED',
                                   'SETTLED_AUTHORITY','ABORTED_BEFORE_COMMIT')
             )
       ) THEN
        RAISE EXCEPTION 'writer activation is not quiescent';
    END IF;
    v_intent_sequence := p_w1_sequence + 1;
    v_digest := pg_catalog.sha256(p_encrypted_candidate);
    v_intent_hash := pg_catalog.sha256(
        p_w1_hash || pg_catalog.convert_to(
            p_installation_id::text || ':' || p_lineage_id::text || ':' ||
            p_epoch::text || ':' || v_intent_sequence::text || ':' ||
            p_activation_id::text || ':INTENT:' || p_key_id::text || ':INSTALLATION:-',
            'UTF8') || v_digest);
    INSERT INTO claimcore_witness.journal
      (installation_id,lineage_id,epoch,sequence,operation_id,scope_kind,
       subject_case_id,phase,key_id,payload_sha256,previous_hash,entry_hash)
    VALUES (p_installation_id,p_lineage_id,p_epoch,v_intent_sequence,p_activation_id,
            'INSTALLATION',NULL,'INTENT',p_key_id,v_digest,p_w1_hash,v_intent_hash);
    INSERT INTO claimcore_witness.journal_payloads
      (installation_id,sequence,subject_case_id,encrypted_payload)
    VALUES (p_installation_id,v_intent_sequence,NULL,p_encrypted_candidate);
    v_settlement_sequence := v_intent_sequence + 1;
    v_digest := pg_catalog.sha256(p_encrypted_settlement);
    v_settlement_hash := pg_catalog.sha256(
        v_intent_hash || pg_catalog.convert_to(
            p_installation_id::text || ':' || p_lineage_id::text || ':' ||
            p_epoch::text || ':' || v_settlement_sequence::text || ':' ||
            p_activation_id::text || ':SETTLED_AUTHORITY:' || p_key_id::text ||
            ':INSTALLATION:-', 'UTF8') || v_digest);
    INSERT INTO claimcore_witness.journal
      (installation_id,lineage_id,epoch,sequence,operation_id,scope_kind,
       subject_case_id,phase,key_id,payload_sha256,previous_hash,entry_hash)
    VALUES (p_installation_id,p_lineage_id,p_epoch,v_settlement_sequence,p_activation_id,
            'INSTALLATION',NULL,'SETTLED_AUTHORITY',p_key_id,v_digest,
            v_intent_hash,v_settlement_hash);
    INSERT INTO claimcore_witness.journal_payloads
      (installation_id,sequence,subject_case_id,encrypted_payload)
    VALUES (p_installation_id,v_settlement_sequence,NULL,p_encrypted_settlement);
    UPDATE claimcore_witness.writer_handoffs
       SET activation_id=p_activation_id,
           activation_candidate_sha256=pg_catalog.sha256(p_canonical),
           activation_intent_sequence=v_intent_sequence,
           activation_intent_hash=v_intent_hash,
           activation_sequence=v_settlement_sequence,
           activation_hash=v_settlement_hash,
           activation_canonical=p_canonical
     WHERE handoff_id=p_handoff_id;
    UPDATE claimcore_witness.installation
       SET activation_pending=false,activation_event_id=p_activation_id,
           activation_sequence=v_settlement_sequence,
           activation_hash=v_settlement_hash,
           tip_sequence=v_settlement_sequence,tip_hash=v_settlement_hash
     WHERE singleton;
    intent_sequence := v_intent_sequence;
    intent_hash := v_intent_hash;
    settlement_sequence := v_settlement_sequence;
    settlement_hash := v_settlement_hash;
    RETURN NEXT;
END
$writer_activation$;

-- One owner-only, monotonic real-data activation. W2-like exact readback keeps a witness-
-- settled/primary-missing crash quarantined until the original primary ticket is repaired.
CREATE FUNCTION claimcore_witness.activate_data_use(
    p_installation_id uuid, p_lineage_id uuid, p_epoch bigint,
    p_activation_id uuid, p_expected_sequence bigint, p_expected_hash bytea,
    p_canonical bytea, p_key_id uuid,
    p_encrypted_candidate bytea, p_encrypted_settlement bytea
) RETURNS TABLE(intent_sequence bigint, intent_hash bytea,
                settlement_sequence bigint, settlement_hash bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $data_use_activation$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_intent_sequence bigint;
    v_intent_hash bytea;
    v_settlement_sequence bigint;
    v_settlement_hash bytea;
    v_digest bytea;
BEGIN
    IF session_user <> 'claimcore_witness_owner'
       OR p_activation_id IS NULL OR p_expected_sequence IS NULL OR p_expected_sequence < 0
       OR p_expected_hash IS NULL OR octet_length(p_expected_hash) <> 32
       OR p_canonical IS NULL OR octet_length(p_canonical) NOT BETWEEN 1 AND 8192
       OR p_key_id IS NULL OR p_encrypted_candidate IS NULL
       OR octet_length(p_encrypted_candidate) NOT BETWEEN 1 AND 1048576
       OR p_encrypted_settlement IS NULL
       OR octet_length(p_encrypted_settlement) NOT BETWEEN 1 AND 1048576 THEN
        RAISE EXCEPTION 'invalid data-use activation';
    END IF;

    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    IF v_installation.installation_id <> p_installation_id
       OR v_installation.lineage_id <> p_lineage_id
       OR v_installation.epoch <> p_epoch
       OR v_installation.data_use_scope <> 'REAL_DATA'
       OR v_installation.active_key_id <> p_key_id THEN
        RAISE EXCEPTION 'data-use installation identity diverged';
    END IF;

    IF v_installation.data_use_phase = 'ACTIVE' THEN
        IF v_installation.data_use_activation_event_id <> p_activation_id
           OR v_installation.data_use_activation_canonical <> p_canonical
           OR v_installation.data_use_activation_sequence
              <> v_installation.data_use_activation_intent_sequence + 1
           OR NOT EXISTS (
               SELECT 1 FROM claimcore_witness.journal i
               JOIN claimcore_witness.journal s ON s.installation_id=i.installation_id
                 AND s.operation_id=i.operation_id AND s.phase='SETTLED_AUTHORITY'
               WHERE i.installation_id=p_installation_id
                 AND i.operation_id=p_activation_id AND i.phase='INTENT'
                 AND i.sequence=v_installation.data_use_activation_intent_sequence
                 AND i.entry_hash=v_installation.data_use_activation_intent_hash
                 AND s.sequence=v_installation.data_use_activation_sequence
                 AND s.entry_hash=v_installation.data_use_activation_hash
           ) THEN
            RAISE EXCEPTION 'divergent data-use activation retry';
        END IF;
        intent_sequence := v_installation.data_use_activation_intent_sequence;
        intent_hash := v_installation.data_use_activation_intent_hash;
        settlement_sequence := v_installation.data_use_activation_sequence;
        settlement_hash := v_installation.data_use_activation_hash;
        RETURN NEXT;
        RETURN;
    END IF;

    IF v_installation.data_use_phase <> 'BOOTSTRAP_NO_CASES'
       OR v_installation.handoff_pending OR v_installation.activation_pending
       OR v_installation.tip_sequence <> p_expected_sequence
       OR v_installation.tip_hash <> p_expected_hash
       OR EXISTS (
           SELECT 1 FROM claimcore_witness.journal i
           WHERE i.installation_id=p_installation_id AND i.phase='INTENT'
             AND NOT EXISTS (
                 SELECT 1 FROM claimcore_witness.journal s
                 WHERE s.installation_id=i.installation_id
                   AND s.operation_id=i.operation_id
                   AND s.phase IN ('SETTLED_ACCEPTED','SETTLED_REVOKED',
                                   'SETTLED_AUTHORITY','ABORTED_BEFORE_COMMIT')
             )
       ) THEN
        RAISE EXCEPTION 'data-use activation is not quiescent';
    END IF;

    v_intent_sequence := p_expected_sequence + 1;
    v_digest := pg_catalog.sha256(p_encrypted_candidate);
    v_intent_hash := pg_catalog.sha256(
        p_expected_hash || pg_catalog.convert_to(
            p_installation_id::text || ':' || p_lineage_id::text || ':' ||
            p_epoch::text || ':' || v_intent_sequence::text || ':' ||
            p_activation_id::text || ':INTENT:' || p_key_id::text || ':INSTALLATION:-',
            'UTF8') || v_digest);
    INSERT INTO claimcore_witness.journal
      (installation_id,lineage_id,epoch,sequence,operation_id,scope_kind,
       subject_case_id,phase,key_id,payload_sha256,previous_hash,entry_hash)
    VALUES (p_installation_id,p_lineage_id,p_epoch,v_intent_sequence,p_activation_id,
            'INSTALLATION',NULL,'INTENT',p_key_id,v_digest,p_expected_hash,v_intent_hash);
    INSERT INTO claimcore_witness.journal_payloads
      (installation_id,sequence,subject_case_id,encrypted_payload)
    VALUES (p_installation_id,v_intent_sequence,NULL,p_encrypted_candidate);

    v_settlement_sequence := v_intent_sequence + 1;
    v_digest := pg_catalog.sha256(p_encrypted_settlement);
    v_settlement_hash := pg_catalog.sha256(
        v_intent_hash || pg_catalog.convert_to(
            p_installation_id::text || ':' || p_lineage_id::text || ':' ||
            p_epoch::text || ':' || v_settlement_sequence::text || ':' ||
            p_activation_id::text || ':SETTLED_AUTHORITY:' || p_key_id::text ||
            ':INSTALLATION:-', 'UTF8') || v_digest);
    INSERT INTO claimcore_witness.journal
      (installation_id,lineage_id,epoch,sequence,operation_id,scope_kind,
       subject_case_id,phase,key_id,payload_sha256,previous_hash,entry_hash)
    VALUES (p_installation_id,p_lineage_id,p_epoch,v_settlement_sequence,p_activation_id,
            'INSTALLATION',NULL,'SETTLED_AUTHORITY',p_key_id,v_digest,
            v_intent_hash,v_settlement_hash);
    INSERT INTO claimcore_witness.journal_payloads
      (installation_id,sequence,subject_case_id,encrypted_payload)
    VALUES (p_installation_id,v_settlement_sequence,NULL,p_encrypted_settlement);
    UPDATE claimcore_witness.installation SET
        data_use_phase='ACTIVE',
        data_use_activation_event_id=p_activation_id,
        data_use_activation_intent_sequence=v_intent_sequence,
        data_use_activation_intent_hash=v_intent_hash,
        data_use_activation_sequence=v_settlement_sequence,
        data_use_activation_hash=v_settlement_hash,
        data_use_activation_canonical=p_canonical,
        tip_sequence=v_settlement_sequence,tip_hash=v_settlement_hash
      WHERE singleton;
    intent_sequence := v_intent_sequence;
    intent_hash := v_intent_hash;
    settlement_sequence := v_settlement_sequence;
    settlement_hash := v_settlement_hash;
    RETURN NEXT;
END
$data_use_activation$;

-- A reviewed abort records the two independently signed owner decisions but keeps the
-- pending fence raised. Only a later exact primary receipt can authorize release.
CREATE FUNCTION claimcore_witness.abort_writer_handoff(
    p_installation_id uuid,
    p_lineage_id uuid,
    p_epoch bigint,
    p_handoff_id uuid,
    p_prepare_sequence bigint,
    p_prepare_hash bytea,
    p_old_writer_capability bytea,
    p_abort_canonical bytea,
    p_abort_signature_one bytea,
    p_abort_signature_two bytea,
    p_abort_signing_key_one uuid,
    p_abort_signing_key_two uuid,
    p_key_id uuid,
    p_encrypted_abort bytea
) RETURNS TABLE(sequence bigint, entry_hash bytea, payload_sha256 bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $handoff_abort$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_handoff claimcore_witness.writer_handoffs%ROWTYPE;
    v_sequence bigint;
    v_hash bytea;
    v_payload_hash bytea;
    v_candidate_hash bytea;
BEGIN
    IF session_user <> 'claimcore_witness_owner'
       OR p_handoff_id IS NULL OR p_handoff_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_prepare_sequence IS NULL OR p_prepare_sequence < 1
       OR p_prepare_hash IS NULL OR octet_length(p_prepare_hash) <> 32
       OR p_old_writer_capability IS NULL OR octet_length(p_old_writer_capability) <> 32
       OR p_abort_canonical IS NULL OR octet_length(p_abort_canonical) NOT BETWEEN 1 AND 16384
       OR p_abort_signature_one IS NULL OR octet_length(p_abort_signature_one) <> 64
       OR p_abort_signature_two IS NULL OR octet_length(p_abort_signature_two) <> 64
       OR p_abort_signing_key_one IS NULL OR p_abort_signing_key_two IS NULL
       OR p_abort_signing_key_one = p_abort_signing_key_two
       OR p_key_id IS NULL OR p_encrypted_abort IS NULL
       OR octet_length(p_encrypted_abort) NOT BETWEEN 1 AND 1048576
    THEN
        RAISE EXCEPTION 'invalid writer handoff abort';
    END IF;
    v_candidate_hash := pg_catalog.sha256(
        pg_catalog.convert_to('CLAIMCORE_WRITER_HANDOFF_ABORT_V1:', 'UTF8') ||
        p_abort_canonical || p_abort_signature_one || p_abort_signature_two);
    v_payload_hash := pg_catalog.sha256(p_encrypted_abort);
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    SELECT * INTO STRICT v_handoff
      FROM claimcore_witness.writer_handoffs h WHERE h.handoff_id=p_handoff_id FOR UPDATE;
    IF v_installation.installation_id <> p_installation_id
       OR v_installation.lineage_id <> p_lineage_id OR v_installation.epoch <> p_epoch
       OR v_installation.active_key_id <> p_key_id
       OR v_handoff.prepare_sequence <> p_prepare_sequence
       OR v_handoff.prepare_hash <> p_prepare_hash
       OR v_handoff.settlement_sequence IS NOT NULL
       OR v_installation.writer_generation <> v_handoff.old_generation
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256(p_old_writer_capability)
       OR NOT EXISTS (
           SELECT 1 FROM claimcore_witness.journal j
           WHERE j.installation_id=p_installation_id AND j.operation_id=p_handoff_id
             AND j.phase='INTENT' AND j.sequence=p_prepare_sequence
             AND j.entry_hash=p_prepare_hash AND j.scope_kind='INSTALLATION'
       ) THEN
        RAISE EXCEPTION 'writer handoff abort prerequisites diverged';
    END IF;
    IF v_handoff.abort_sequence IS NOT NULL THEN
        IF v_handoff.abort_canonical <> p_abort_canonical
           OR v_handoff.abort_signature_one <> p_abort_signature_one
           OR v_handoff.abort_signature_two <> p_abort_signature_two
           OR v_handoff.abort_signing_key_one <> p_abort_signing_key_one
           OR v_handoff.abort_signing_key_two <> p_abort_signing_key_two
           OR v_handoff.abort_candidate_sha256 <> v_candidate_hash
           OR NOT EXISTS (
               SELECT 1 FROM claimcore_witness.journal j
               JOIN claimcore_witness.journal_payloads p
                 ON p.installation_id=j.installation_id AND p.sequence=j.sequence
               WHERE j.installation_id=p_installation_id AND j.operation_id=p_handoff_id
                 AND j.phase='ABORTED_BEFORE_COMMIT'
                 AND j.sequence=v_handoff.abort_sequence
                 AND j.entry_hash=v_handoff.abort_hash
                 AND j.payload_sha256=v_payload_hash
                 AND p.encrypted_payload=p_encrypted_abort
           ) THEN
            RAISE EXCEPTION 'divergent writer handoff abort retry';
        END IF;
        sequence := v_handoff.abort_sequence;
        entry_hash := v_handoff.abort_hash;
        payload_sha256 := v_payload_hash;
        RETURN NEXT;
        RETURN;
    END IF;
    IF NOT v_installation.handoff_pending
       OR v_installation.tip_sequence <> p_prepare_sequence
       OR v_installation.tip_hash <> p_prepare_hash THEN
        RAISE EXCEPTION 'writer handoff abort tip diverged';
    END IF;
    v_sequence := p_prepare_sequence + 1;
    v_hash := pg_catalog.sha256(
        p_prepare_hash || pg_catalog.convert_to(
            p_installation_id::text || ':' || p_lineage_id::text || ':' ||
            p_epoch::text || ':' || v_sequence::text || ':' ||
            p_handoff_id::text || ':ABORTED_BEFORE_COMMIT:' || p_key_id::text ||
            ':INSTALLATION:-', 'UTF8') || v_payload_hash);
    INSERT INTO claimcore_witness.journal
      (installation_id,lineage_id,epoch,sequence,operation_id,scope_kind,
       subject_case_id,phase,key_id,payload_sha256,previous_hash,entry_hash)
    VALUES
      (p_installation_id,p_lineage_id,p_epoch,v_sequence,p_handoff_id,'INSTALLATION',
       NULL,'ABORTED_BEFORE_COMMIT',p_key_id,v_payload_hash,p_prepare_hash,v_hash);
    INSERT INTO claimcore_witness.journal_payloads
      (installation_id,sequence,subject_case_id,encrypted_payload)
    VALUES (p_installation_id,v_sequence,NULL,p_encrypted_abort);
    UPDATE claimcore_witness.writer_handoffs
       SET abort_candidate_sha256=v_candidate_hash,abort_sequence=v_sequence,
           abort_hash=v_hash,abort_canonical=p_abort_canonical,
           abort_signature_one=p_abort_signature_one,
           abort_signature_two=p_abort_signature_two,
           abort_signing_key_one=p_abort_signing_key_one,
           abort_signing_key_two=p_abort_signing_key_two
     WHERE handoff_id=p_handoff_id;
    UPDATE claimcore_witness.installation
       SET tip_sequence=v_sequence,tip_hash=v_hash WHERE singleton;
    sequence := v_sequence;
    entry_hash := v_hash;
    payload_sha256 := v_payload_hash;
    RETURN NEXT;
END
$handoff_abort$;

-- This final owner-only release does not append a new journal event. Runtime independently
-- compares the exact abort ticket with a primary receipt before admitting disclosure.
CREATE FUNCTION claimcore_witness.release_aborted_writer_handoff(
    p_installation_id uuid,
    p_lineage_id uuid,
    p_epoch bigint,
    p_handoff_id uuid,
    p_abort_sequence bigint,
    p_abort_hash bytea,
    p_old_writer_capability bytea
) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $handoff_abort_release$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_handoff claimcore_witness.writer_handoffs%ROWTYPE;
BEGIN
    IF session_user <> 'claimcore_witness_owner'
       OR p_handoff_id IS NULL OR p_abort_sequence IS NULL OR p_abort_sequence < 1
       OR p_abort_hash IS NULL OR octet_length(p_abort_hash) <> 32
       OR p_old_writer_capability IS NULL OR octet_length(p_old_writer_capability) <> 32
    THEN
        RAISE EXCEPTION 'invalid writer handoff abort release';
    END IF;
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    SELECT * INTO STRICT v_handoff
      FROM claimcore_witness.writer_handoffs h WHERE h.handoff_id=p_handoff_id FOR UPDATE;
    IF v_installation.installation_id <> p_installation_id
       OR v_installation.lineage_id <> p_lineage_id OR v_installation.epoch <> p_epoch
       OR v_installation.writer_generation <> v_handoff.old_generation
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256(p_old_writer_capability)
       OR v_handoff.settlement_sequence IS NOT NULL
       OR v_handoff.abort_sequence <> p_abort_sequence
       OR v_handoff.abort_hash <> p_abort_hash
       OR v_installation.tip_sequence <> p_abort_sequence
       OR v_installation.tip_hash <> p_abort_hash THEN
        RAISE EXCEPTION 'writer handoff abort release diverged';
    END IF;
    IF NOT v_installation.handoff_pending THEN
        IF v_installation.last_aborted_handoff_id <> p_handoff_id
           OR v_installation.last_aborted_handoff_sequence <> p_abort_sequence
           OR v_installation.last_aborted_handoff_hash <> p_abort_hash THEN
            RAISE EXCEPTION 'divergent writer handoff abort release retry';
        END IF;
        RETURN true;
    END IF;
    UPDATE claimcore_witness.installation
       SET handoff_pending=false,last_aborted_handoff_id=p_handoff_id,
           last_aborted_handoff_sequence=p_abort_sequence,
           last_aborted_handoff_hash=p_abort_hash
     WHERE singleton;
    RETURN true;
END
$handoff_abort_release$;

-- Schema-owner only: the immutable settlement and exact CASE ciphertext removal commit together.
-- Metadata remains hash-linked. The rolling target digest is bounded by one journal row, not
-- an unbounded aggregate or a caller-supplied list of sequence IDs.
CREATE FUNCTION claimcore_witness.settle_and_prune(
    p_installation_id uuid,
    p_lineage_id uuid,
    p_epoch bigint,
    p_prune_event_id uuid,
    p_case_id uuid,
    p_prune_intent_sequence bigint,
    p_prune_intent_hash bytea,
    p_purge_event_id uuid,
    p_purge_intent_sequence bigint,
    p_purge_intent_hash bytea,
    p_cutoff_sequence bigint,
    p_cutoff_hash bytea,
    p_target_count bigint,
    p_target_digest bytea,
    p_approval_one_id uuid,
    p_approval_one_sequence bigint,
    p_approval_one_hash bytea,
    p_approval_two_id uuid,
    p_approval_two_sequence bigint,
    p_approval_two_hash bytea,
    p_key_id uuid,
    p_encrypted_settlement bytea,
    p_writer_capability bytea
) RETURNS TABLE(sequence bigint, entry_hash bytea, payload_sha256 bytea, deleted_count bigint)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $prune$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_intent claimcore_witness.journal%ROWTYPE;
    v_purge claimcore_witness.journal%ROWTYPE;
    v_purge_settled claimcore_witness.journal%ROWTYPE;
    v_cutoff claimcore_witness.journal%ROWTYPE;
    v_existing claimcore_witness.journal%ROWTYPE;
    v_target claimcore_witness.journal%ROWTYPE;
    v_post claimcore_witness.journal%ROWTYPE;
    v_one_intent claimcore_witness.journal%ROWTYPE;
    v_two_intent claimcore_witness.journal%ROWTYPE;
    v_one_settled claimcore_witness.journal%ROWTYPE;
    v_two_settled claimcore_witness.journal%ROWTYPE;
    v_digest bytea;
    v_count bigint := 0;
    v_deleted bigint := 0;
    v_one_rows integer := 0;
    v_two_rows integer := 0;
    v_prune_rows integer := 0;
BEGIN
    IF session_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'witness ciphertext prune requires schema owner';
    END IF;
    IF p_installation_id IS NULL OR p_lineage_id IS NULL OR p_epoch IS NULL
       OR p_prune_event_id IS NULL OR p_case_id IS NULL OR p_purge_event_id IS NULL
       OR p_installation_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_lineage_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_prune_event_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_case_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_purge_event_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_key_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_key_id IS NULL OR p_prune_intent_sequence IS NULL OR p_cutoff_sequence IS NULL
       OR p_purge_intent_sequence IS NULL OR p_target_count IS NULL
       OR p_approval_one_id IS NULL OR p_approval_two_id IS NULL
       OR p_approval_one_id = p_approval_two_id
       OR p_approval_one_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_approval_two_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_approval_one_sequence IS NULL OR p_approval_two_sequence IS NULL
       OR p_approval_one_hash IS NULL OR p_approval_two_hash IS NULL
       OR p_prune_intent_hash IS NULL OR p_purge_intent_hash IS NULL
       OR p_cutoff_hash IS NULL OR p_target_digest IS NULL
       OR p_encrypted_settlement IS NULL
       OR p_cutoff_sequence < 1 OR p_target_count < 1
       OR p_prune_intent_sequence <= p_cutoff_sequence
       OR p_purge_intent_sequence > p_cutoff_sequence
       OR octet_length(p_prune_intent_hash) <> 32
       OR octet_length(p_purge_intent_hash) <> 32
       OR octet_length(p_cutoff_hash) <> 32 OR octet_length(p_target_digest) <> 32
       OR octet_length(p_approval_one_hash) <> 32
       OR octet_length(p_approval_two_hash) <> 32
       OR octet_length(p_encrypted_settlement) NOT BETWEEN 1 AND 1048576
    THEN
        RAISE EXCEPTION 'invalid witness ciphertext prune proof';
    END IF;

    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    IF v_installation.installation_id <> p_installation_id
       OR v_installation.lineage_id <> p_lineage_id
       OR v_installation.epoch <> p_epoch
       OR v_installation.tip_sequence < p_prune_intent_sequence THEN
        RAISE EXCEPTION 'witness prune identity or key diverged';
    END IF;
    IF v_installation.handoff_pending OR v_installation.activation_pending
       OR p_writer_capability IS NULL OR octet_length(p_writer_capability) <> 32
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256(p_writer_capability) THEN
        RAISE EXCEPTION 'witness writer generation is fenced';
    END IF;

    SELECT j.* INTO STRICT v_intent FROM claimcore_witness.journal j
      WHERE j.installation_id=p_installation_id AND j.sequence=p_prune_intent_sequence;
    SELECT j.* INTO STRICT v_purge FROM claimcore_witness.journal j
      WHERE j.installation_id=p_installation_id AND j.sequence=p_purge_intent_sequence;
    SELECT j.* INTO STRICT v_purge_settled FROM claimcore_witness.journal j
      WHERE j.installation_id=p_installation_id
        AND j.operation_id=p_purge_event_id AND j.phase='SETTLED_AUTHORITY';
    SELECT j.* INTO STRICT v_cutoff FROM claimcore_witness.journal j
      WHERE j.installation_id=p_installation_id AND j.sequence=p_cutoff_sequence;
    IF v_intent.lineage_id <> p_lineage_id OR v_intent.epoch <> p_epoch
       OR v_intent.operation_id <> p_prune_event_id OR v_intent.phase <> 'INTENT'
       OR v_intent.scope_kind <> 'CASE' OR v_intent.subject_case_id <> p_case_id
       OR v_intent.entry_hash <> p_prune_intent_hash OR v_intent.key_id <> p_key_id
       OR v_purge.operation_id <> p_purge_event_id
       OR v_purge.phase <> 'INTENT'
       OR v_purge.subject_case_id <> p_case_id
       OR v_purge.entry_hash <> p_purge_intent_hash
       OR v_purge.scope_kind <> 'CASE' OR v_purge.epoch <> p_epoch
       OR v_purge_settled.subject_case_id <> p_case_id
       OR v_purge_settled.scope_kind <> 'CASE'
       OR v_purge_settled.epoch <> p_epoch
       OR v_purge_settled.sequence <= v_purge.sequence
       OR v_purge_settled.sequence > p_cutoff_sequence
       OR v_cutoff.entry_hash <> p_cutoff_hash
       OR v_cutoff.epoch <> p_epoch THEN
        RAISE EXCEPTION 'witness prune source proof diverged';
    END IF;

    SELECT j.* INTO v_existing FROM claimcore_witness.journal j
      WHERE j.installation_id=p_installation_id
        AND j.operation_id=p_prune_event_id AND j.phase='SETTLED_AUTHORITY';
    IF v_existing.operation_id IS NULL THEN
        IF v_installation.active_key_id <> p_key_id THEN
            RAISE EXCEPTION 'new witness prune settlement requires active key';
        END IF;
    ELSIF v_existing.lineage_id <> p_lineage_id
       OR v_existing.epoch <> p_epoch
       OR v_existing.sequence <= v_intent.sequence
       OR v_existing.scope_kind <> 'CASE'
       OR v_existing.subject_case_id <> p_case_id
       OR v_existing.key_id <> p_key_id
       OR v_existing.payload_sha256 <> pg_catalog.sha256(p_encrypted_settlement)
       OR NOT EXISTS (
           SELECT 1 FROM claimcore_witness.journal_payloads p
           WHERE p.installation_id=v_existing.installation_id
             AND p.sequence=v_existing.sequence
             AND p.subject_case_id=p_case_id
             AND p.encrypted_payload=p_encrypted_settlement
       ) THEN
        RAISE EXCEPTION 'existing witness prune settlement diverged';
    END IF;
    SELECT j.* INTO STRICT v_one_intent FROM claimcore_witness.journal j
      WHERE j.installation_id=p_installation_id AND j.sequence=p_approval_one_sequence;
    SELECT j.* INTO STRICT v_two_intent FROM claimcore_witness.journal j
      WHERE j.installation_id=p_installation_id AND j.sequence=p_approval_two_sequence;
    SELECT j.* INTO STRICT v_one_settled FROM claimcore_witness.journal j
      WHERE j.installation_id=p_installation_id
        AND j.operation_id=p_approval_one_id AND j.phase='SETTLED_AUTHORITY';
    SELECT j.* INTO STRICT v_two_settled FROM claimcore_witness.journal j
      WHERE j.installation_id=p_installation_id
        AND j.operation_id=p_approval_two_id AND j.phase='SETTLED_AUTHORITY';
    IF v_one_intent.operation_id <> p_approval_one_id
       OR v_one_intent.phase <> 'INTENT' OR v_one_intent.entry_hash <> p_approval_one_hash
       OR v_two_intent.operation_id <> p_approval_two_id
       OR v_two_intent.phase <> 'INTENT' OR v_two_intent.entry_hash <> p_approval_two_hash
       OR v_one_intent.scope_kind <> 'CASE' OR v_two_intent.scope_kind <> 'CASE'
       OR v_one_intent.subject_case_id <> p_case_id
       OR v_two_intent.subject_case_id <> p_case_id
       OR v_one_intent.sequence <= p_cutoff_sequence
       OR v_two_intent.sequence <= p_cutoff_sequence
       OR v_one_settled.subject_case_id <> p_case_id
       OR v_two_settled.subject_case_id <> p_case_id
       OR v_one_settled.sequence <= v_one_intent.sequence
       OR v_two_settled.sequence <= v_two_intent.sequence
       OR v_one_settled.sequence >= v_intent.sequence
       OR v_two_settled.sequence >= v_intent.sequence THEN
        RAISE EXCEPTION 'witness prune approvals diverged';
    END IF;
    FOR v_post IN SELECT j.* FROM claimcore_witness.journal j
        WHERE j.installation_id=p_installation_id AND j.scope_kind='CASE'
          AND j.subject_case_id=p_case_id AND j.sequence>p_cutoff_sequence
          AND (v_existing.operation_id IS NULL OR j.sequence<=v_existing.sequence)
        ORDER BY j.sequence
    LOOP
        IF v_post.operation_id=p_approval_one_id
           AND v_post.phase IN ('INTENT','SETTLED_AUTHORITY') THEN
            v_one_rows := v_one_rows + 1;
        ELSIF v_post.operation_id=p_approval_two_id
              AND v_post.phase IN ('INTENT','SETTLED_AUTHORITY') THEN
            v_two_rows := v_two_rows + 1;
        ELSIF v_post.operation_id=p_prune_event_id
              AND v_post.phase IN ('INTENT','SETTLED_AUTHORITY') THEN
            v_prune_rows := v_prune_rows + 1;
        ELSE
            RAISE EXCEPTION 'unexpected post-cutoff CASE authority';
        END IF;
    END LOOP;
    IF v_one_rows <> 2 OR v_two_rows <> 2
       OR (v_existing.operation_id IS NULL AND v_prune_rows <> 1)
       OR (v_existing.operation_id IS NOT NULL AND v_prune_rows <> 2) THEN
        RAISE EXCEPTION 'witness prune post-cutoff phase set diverged';
    END IF;
    v_digest := pg_catalog.sha256(
        pg_catalog.convert_to('claimcore:witness-prune-targets:v1', 'UTF8')
        || pg_catalog.decode('00', 'hex'));
    FOR v_target IN SELECT j.* FROM claimcore_witness.journal j
        WHERE j.installation_id=p_installation_id AND j.scope_kind='CASE'
          AND j.subject_case_id=p_case_id AND j.sequence<=p_cutoff_sequence
        ORDER BY j.sequence
    LOOP
        v_digest := pg_catalog.sha256(
            v_digest || pg_catalog.int8send(v_target.sequence)
            || pg_catalog.convert_to(v_target.operation_id::text, 'UTF8')
            || pg_catalog.convert_to(v_target.phase, 'UTF8')
            || pg_catalog.int8send(v_target.epoch)
            || v_target.entry_hash || v_target.payload_sha256);
        v_count := v_count + 1;
        IF (v_existing.operation_id IS NULL) <> EXISTS (
            SELECT 1 FROM claimcore_witness.journal_payloads p
            WHERE p.installation_id=v_target.installation_id
              AND p.sequence=v_target.sequence AND p.subject_case_id=p_case_id
              AND pg_catalog.sha256(p.encrypted_payload)=v_target.payload_sha256
        ) THEN
            RAISE EXCEPTION 'witness prune payload state is inconsistent';
        END IF;
    END LOOP;
    IF v_count <> p_target_count OR v_digest <> p_target_digest THEN
        RAISE EXCEPTION 'witness prune target set diverged';
    END IF;

    -- A replay checks the sealed historical phase set and physical absence above, then returns
    -- the exact settled ticket. Later legitimate CASE control events and key rotation cannot
    -- rebase this irreversible event; a fresh settlement still requires the current key.
    IF v_existing.operation_id IS NOT NULL THEN
        sequence := v_existing.sequence;
        entry_hash := v_existing.entry_hash;
        payload_sha256 := v_existing.payload_sha256;
        deleted_count := 0;
        RETURN NEXT;
        RETURN;
    END IF;

    SELECT appended.sequence, appended.entry_hash, appended.payload_sha256
      INTO sequence, entry_hash, payload_sha256
      FROM claimcore_witness.append(
        p_installation_id, p_lineage_id, p_epoch, p_prune_event_id, NULL, NULL,
        'SETTLED_AUTHORITY', p_key_id, p_encrypted_settlement,
        p_writer_capability) AS appended;
    IF v_existing.operation_id IS NULL THEN
        DELETE FROM claimcore_witness.journal_payloads p
          USING claimcore_witness.journal j
          WHERE p.installation_id=p_installation_id
            AND p.installation_id=j.installation_id AND p.sequence=j.sequence
            AND j.scope_kind='CASE' AND j.subject_case_id=p_case_id
            AND j.sequence<=p_cutoff_sequence;
        GET DIAGNOSTICS v_deleted = ROW_COUNT;
        IF v_deleted <> p_target_count THEN
            RAISE EXCEPTION 'witness prune deleted a divergent set';
        END IF;
    END IF;
    deleted_count := v_deleted;
    RETURN NEXT;
END
$prune$;

CREATE FUNCTION claimcore_witness.rotate_key(
    p_installation_id uuid,
    p_lineage_id uuid,
    p_epoch bigint,
    p_operation_id uuid,
    p_old_key_id uuid,
    p_new_key_id uuid,
    p_new_key_check bytea,
    p_rotation_envelope bytea,
    p_writer_capability bytea
) RETURNS TABLE(sequence bigint, entry_hash bytea, payload_sha256 bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $rotation$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
BEGIN
    IF p_old_key_id IS NULL OR p_new_key_id IS NULL OR p_old_key_id = p_new_key_id
       OR p_operation_id IS NULL OR p_new_key_check IS NULL
       OR octet_length(p_new_key_check) NOT BETWEEN 32 AND 4096
    THEN
        RAISE EXCEPTION 'invalid witness key rotation';
    END IF;
    IF session_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'witness key rotation requires schema owner';
    END IF;
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    IF v_installation.installation_id <> p_installation_id
       OR v_installation.lineage_id <> p_lineage_id
       OR v_installation.epoch <> p_epoch
       OR v_installation.active_key_id <> p_old_key_id THEN
        RAISE EXCEPTION 'witness key rotation identity mismatch';
    END IF;
    IF v_installation.handoff_pending OR v_installation.activation_pending
       OR p_writer_capability IS NULL OR octet_length(p_writer_capability) <> 32
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256(p_writer_capability) THEN
        RAISE EXCEPTION 'witness writer generation is fenced';
    END IF;
    UPDATE claimcore_witness.installation
       SET active_key_id = p_new_key_id,
           key_check_envelope = p_new_key_check WHERE singleton;
    RETURN QUERY SELECT appended.sequence, appended.entry_hash, appended.payload_sha256
      FROM claimcore_witness.append(
        p_installation_id, p_lineage_id, p_epoch, p_operation_id, 'INSTALLATION', NULL,
        'KEY_ROTATED', p_new_key_id, p_rotation_envelope,
        p_writer_capability) AS appended;
END
$rotation$;

REVOKE ALL ON ALL TABLES IN SCHEMA claimcore_witness FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA claimcore_witness FROM PUBLIC;
GRANT USAGE ON SCHEMA claimcore_witness TO claimcore_witness_writer;
GRANT USAGE ON SCHEMA claimcore_witness TO claimcore_witness_auditor;
GRANT SELECT ON claimcore_witness.installation, claimcore_witness.journal,
    claimcore_witness.journal_payloads, claimcore_witness.writer_handoffs
  TO claimcore_witness_writer;
GRANT SELECT ON claimcore_witness.installation, claimcore_witness.journal,
    claimcore_witness.journal_payloads, claimcore_witness.writer_handoffs
  TO claimcore_witness_auditor;
GRANT EXECUTE ON FUNCTION claimcore_witness.append(uuid, uuid, bigint, uuid, text, uuid, text, uuid, bytea, bytea)
  TO claimcore_witness_writer;
GRANT EXECUTE ON FUNCTION claimcore_witness.acquire_read_fence(uuid, uuid, bigint, bytea)
  TO claimcore_witness_writer;
