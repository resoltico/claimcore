
CREATE FUNCTION claimcore_witness.insert_journal_entry(p_identity claimcore_witness.installation_identity, p_previous claimcore_witness.journal_tip, p_subject claimcore_witness.journal_subject, p_request claimcore_witness.journal_request)
RETURNS TABLE(sequence bigint,entry_hash bytea,payload_sha256 bytea)
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    sequence := (p_previous).sequence + 1;
    payload_sha256 := pg_catalog.sha256((p_request).encrypted_payload);
    entry_hash := pg_catalog.sha256(
        (p_previous).entry_hash || pg_catalog.convert_to(
            (p_identity).installation_id::text || ':' || (p_identity).lineage_id::text || ':' ||
            (p_identity).epoch::text || ':' || sequence::text || ':' ||
            (p_request).operation_id::text || ':' || (p_request).phase || ':' ||
            (p_request).key_id::text || ':' || (p_subject).scope_kind || ':' ||
            COALESCE((p_subject).subject_case_id::text, '-'), 'UTF8') || payload_sha256);
    INSERT INTO claimcore_witness.journal
        (installation_id,lineage_id,epoch,sequence,operation_id,scope_kind,subject_case_id,
         phase,key_id,payload_sha256,previous_hash,entry_hash)
    VALUES ((p_identity).installation_id,(p_identity).lineage_id,(p_identity).epoch,
        sequence,(p_request).operation_id,(p_subject).scope_kind,(p_subject).subject_case_id,
        (p_request).phase,(p_request).key_id,payload_sha256,(p_previous).entry_hash,entry_hash);
    INSERT INTO claimcore_witness.journal_payloads
        (installation_id,sequence,subject_case_id,encrypted_payload)
    VALUES ((p_identity).installation_id,sequence,(p_subject).subject_case_id,(p_request).encrypted_payload);
    RETURN NEXT;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_append_request(p_identity claimcore_witness.installation_identity, p_subject claimcore_witness.journal_subject, p_request claimcore_witness.journal_request, p_writer_capability bytea)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    PERFORM claimcore_witness.validate_installation_identity(p_identity);
    PERFORM claimcore_witness.validate_journal_request(p_request);
    IF (p_identity).installation_id IS NULL OR (p_identity).lineage_id IS NULL OR (p_identity).epoch IS NULL
       OR (p_request).operation_id IS NULL OR (p_request).phase IS NULL OR (p_request).key_id IS NULL
       OR (p_request).encrypted_payload IS NULL
       OR octet_length((p_request).encrypted_payload) NOT BETWEEN 1 AND 1048576
       OR p_writer_capability IS NULL OR octet_length(p_writer_capability) <> 32
       OR (p_request).phase NOT IN ('INTENT', 'SETTLED_ACCEPTED', 'SETTLED_REVOKED',
                         'SETTLED_AUTHORITY', 'ABORTED_BEFORE_COMMIT', 'KEY_ROTATED')
    THEN
        RAISE EXCEPTION 'invalid witness append';
    END IF;
    IF (p_request).phase = 'KEY_ROTATED' AND session_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'witness key rotation requires schema owner';
    END IF;
    IF (p_subject).subject_case_id = '00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness subject';
    END IF;
    IF (p_request).phase IN ('INTENT', 'KEY_ROTATED') THEN
        IF (p_subject).scope_kind IS NULL THEN
            RAISE EXCEPTION 'invalid witness scope';
        END IF;
        IF NOT (
            ((p_subject).scope_kind = 'CASE' AND (p_subject).subject_case_id IS NOT NULL)
            OR ((p_subject).scope_kind = 'INSTALLATION' AND (p_subject).subject_case_id IS NULL)
        ) OR ((p_request).phase = 'KEY_ROTATED' AND (p_subject).scope_kind <> 'INSTALLATION') THEN
            RAISE EXCEPTION 'invalid witness scope';
        END IF;
    ELSIF (p_subject).scope_kind IS NOT NULL OR (p_subject).subject_case_id IS NOT NULL THEN
        RAISE EXCEPTION 'witness settlement scope must inherit intent';
    END IF;

END
$function$;
CREATE FUNCTION claimcore_witness.require_append_writer(p_identity claimcore_witness.installation_identity, p_request claimcore_witness.journal_request, p_writer_capability bytea)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    SELECT * INTO STRICT v_installation FROM claimcore_witness.installation WHERE singleton;
    IF v_installation.installation_id <> (p_identity).installation_id
       OR v_installation.lineage_id <> (p_identity).lineage_id
       OR v_installation.epoch <> (p_identity).epoch
       OR v_installation.active_key_id <> (p_request).key_id THEN
        RAISE EXCEPTION 'witness identity or epoch mismatch';
    END IF;
    IF v_installation.handoff_pending OR v_installation.activation_pending
       OR v_installation.loss_retirement_pending OR v_installation.loss_retired
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256(p_writer_capability) THEN
        RAISE EXCEPTION 'witness writer generation is fenced';
    END IF;

END
$function$;
CREATE FUNCTION claimcore_witness.resolve_append_subject(p_identity claimcore_witness.installation_identity, p_subject claimcore_witness.journal_subject, p_request claimcore_witness.journal_request)
RETURNS TABLE(scope_kind text,subject_case_id uuid)
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_scope_kind text;
    v_subject_case_id uuid;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    v_scope_kind := (p_subject).scope_kind;
    v_subject_case_id := (p_subject).subject_case_id;
    IF (p_request).phase NOT IN ('INTENT', 'KEY_ROTATED') THEN
        SELECT j.scope_kind, j.subject_case_id INTO v_scope_kind, v_subject_case_id
          FROM claimcore_witness.journal j
          WHERE j.installation_id=(p_identity).installation_id
            AND j.operation_id=(p_request).operation_id AND j.phase='INTENT';
        IF NOT FOUND THEN
            RAISE EXCEPTION 'witness settlement has no intent';
        END IF;
    END IF;

    scope_kind:=v_scope_kind; subject_case_id:=v_subject_case_id; RETURN NEXT;
END
$function$;
CREATE FUNCTION claimcore_witness.find_exact_append(p_identity claimcore_witness.installation_identity, p_request claimcore_witness.journal_request, v_scope_kind text, v_subject_case_id uuid)
RETURNS TABLE(sequence bigint,entry_hash bytea,payload_sha256 bytea)
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_existing claimcore_witness.journal%ROWTYPE;
    v_digest bytea;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    v_digest := pg_catalog.sha256((p_request).encrypted_payload);
    SELECT * INTO v_existing FROM claimcore_witness.journal
      WHERE installation_id = (p_identity).installation_id
        AND operation_id = (p_request).operation_id AND phase = (p_request).phase;
    IF FOUND THEN
        IF v_existing.lineage_id <> (p_identity).lineage_id OR v_existing.epoch <> (p_identity).epoch
           OR v_existing.payload_sha256 <> v_digest
           OR v_existing.key_id <> (p_request).key_id
           OR v_existing.scope_kind <> v_scope_kind
           OR v_existing.subject_case_id IS DISTINCT FROM v_subject_case_id
           OR NOT EXISTS (
               SELECT 1 FROM claimcore_witness.journal_payloads p
               WHERE p.installation_id=v_existing.installation_id
                 AND p.sequence=v_existing.sequence
                 AND p.subject_case_id IS NOT DISTINCT FROM v_subject_case_id
                 AND p.encrypted_payload=(p_request).encrypted_payload
           ) THEN
            RAISE EXCEPTION 'divergent witness retry';
        END IF;
        sequence := v_existing.sequence;
        entry_hash := v_existing.entry_hash;
        payload_sha256 := v_existing.payload_sha256;
        RETURN NEXT;
        RETURN;
    END IF;

END
$function$;
CREATE FUNCTION claimcore_witness.require_unsettled_append(p_identity claimcore_witness.installation_identity, p_request claimcore_witness.journal_request)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_request).phase NOT IN ('INTENT', 'KEY_ROTATED') THEN
        IF NOT EXISTS (
            SELECT 1 FROM claimcore_witness.journal
            WHERE installation_id = (p_identity).installation_id
              AND operation_id = (p_request).operation_id AND phase = 'INTENT'
        ) OR EXISTS (
            SELECT 1 FROM claimcore_witness.journal
            WHERE installation_id = (p_identity).installation_id
              AND operation_id = (p_request).operation_id AND phase <> 'INTENT'
        ) THEN
            RAISE EXCEPTION 'witness settlement requires one unsettled intent';
        END IF;
    END IF;

END
$function$;
CREATE FUNCTION claimcore_witness.append(p_identity claimcore_witness.installation_identity, p_subject claimcore_witness.journal_subject, p_request claimcore_witness.journal_request, p_writer_capability bytea)
RETURNS TABLE(sequence bigint,entry_hash bytea,payload_sha256 bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_digest bytea;
    v_sequence bigint;
    v_hash bytea;
    v_scope_kind text;
    v_subject_case_id uuid;
BEGIN
    PERFORM claimcore_witness.validate_append_request(p_identity,p_subject,p_request,p_writer_capability);
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    PERFORM claimcore_witness.require_append_writer(p_identity,p_request,p_writer_capability);
    SELECT resolved.scope_kind,resolved.subject_case_id INTO v_scope_kind,v_subject_case_id FROM claimcore_witness.resolve_append_subject(p_identity,p_subject,p_request) resolved;
    SELECT exact.sequence,exact.entry_hash,exact.payload_sha256 INTO sequence,entry_hash,payload_sha256 FROM claimcore_witness.find_exact_append(p_identity,p_request,v_scope_kind,v_subject_case_id) exact;
    IF FOUND THEN RETURN NEXT; RETURN; END IF;
    PERFORM claimcore_witness.require_unsettled_append(p_identity,p_request);
    SELECT inserted.sequence,inserted.entry_hash,inserted.payload_sha256
      INTO v_sequence,v_hash,v_digest FROM claimcore_witness.insert_journal_entry(p_identity,ROW(v_installation.tip_sequence,v_installation.tip_hash)::claimcore_witness.journal_tip,ROW(v_scope_kind,v_subject_case_id)::claimcore_witness.journal_subject,ROW((p_request).operation_id,(p_request).phase,(p_request).key_id,(p_request).encrypted_payload)::claimcore_witness.journal_request) inserted;
    UPDATE claimcore_witness.installation
       SET tip_sequence = v_sequence, tip_hash = v_hash WHERE singleton;
    sequence := v_sequence;
    entry_hash := v_hash;
    payload_sha256 := v_digest;
    RETURN NEXT;
END

$function$;
