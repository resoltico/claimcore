CREATE FUNCTION claimcore_witness.verify_prune_approvals(p_identity claimcore_witness.installation_identity, p_source claimcore_witness.prune_source, p_authority claimcore_witness.prune_authority)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_one_intent claimcore_witness.journal%ROWTYPE;
    v_two_intent claimcore_witness.journal%ROWTYPE;
    v_one_settled claimcore_witness.journal%ROWTYPE;
    v_two_settled claimcore_witness.journal%ROWTYPE;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    SELECT j.* INTO STRICT v_one_intent FROM claimcore_witness.journal j
      WHERE j.installation_id=(p_identity).installation_id AND j.sequence=((p_authority).approval_one).sequence;
    SELECT j.* INTO STRICT v_two_intent FROM claimcore_witness.journal j
      WHERE j.installation_id=(p_identity).installation_id AND j.sequence=((p_authority).approval_two).sequence;
    SELECT j.* INTO STRICT v_one_settled FROM claimcore_witness.journal j
      WHERE j.installation_id=(p_identity).installation_id
        AND j.operation_id=((p_authority).approval_one).operation_id AND j.phase='SETTLED_AUTHORITY';
    SELECT j.* INTO STRICT v_two_settled FROM claimcore_witness.journal j
      WHERE j.installation_id=(p_identity).installation_id
        AND j.operation_id=((p_authority).approval_two).operation_id AND j.phase='SETTLED_AUTHORITY';
    IF v_one_intent.operation_id <> ((p_authority).approval_one).operation_id
       OR v_one_intent.phase <> 'INTENT' OR v_one_intent.entry_hash <> ((p_authority).approval_one).entry_hash
       OR v_two_intent.operation_id <> ((p_authority).approval_two).operation_id
       OR v_two_intent.phase <> 'INTENT' OR v_two_intent.entry_hash <> ((p_authority).approval_two).entry_hash
       OR v_one_intent.scope_kind <> 'CASE' OR v_two_intent.scope_kind <> 'CASE'
       OR v_one_intent.subject_case_id <> (p_source).case_id
       OR v_two_intent.subject_case_id <> (p_source).case_id
       OR v_one_intent.sequence <= ((p_source).cutoff).sequence
       OR v_two_intent.sequence <= ((p_source).cutoff).sequence
       OR v_one_settled.subject_case_id <> (p_source).case_id
       OR v_two_settled.subject_case_id <> (p_source).case_id
       OR v_one_settled.sequence <= v_one_intent.sequence
       OR v_two_settled.sequence <= v_two_intent.sequence
       OR v_one_settled.sequence >= ((p_authority).intent).sequence
       OR v_two_settled.sequence >= ((p_authority).intent).sequence THEN
        RAISE EXCEPTION 'witness prune approvals diverged';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.verify_prune_phase_set(p_identity claimcore_witness.installation_identity, p_source claimcore_witness.prune_source, p_authority claimcore_witness.prune_authority, v_existing claimcore_witness.journal)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_post claimcore_witness.journal%ROWTYPE;
    v_one_rows integer := 0;
    v_two_rows integer := 0;
    v_prune_rows integer := 0;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    FOR v_post IN SELECT j.* FROM claimcore_witness.journal j
        WHERE j.installation_id=(p_identity).installation_id AND j.scope_kind='CASE'
          AND j.subject_case_id=(p_source).case_id AND j.sequence>((p_source).cutoff).sequence
          AND (v_existing.operation_id IS NULL OR j.sequence<=v_existing.sequence)
        ORDER BY j.sequence
    LOOP
        IF v_post.operation_id=((p_authority).approval_one).operation_id
           AND v_post.phase IN ('INTENT','SETTLED_AUTHORITY') THEN
            v_one_rows := v_one_rows + 1;
        ELSIF v_post.operation_id=((p_authority).approval_two).operation_id
              AND v_post.phase IN ('INTENT','SETTLED_AUTHORITY') THEN
            v_two_rows := v_two_rows + 1;
        ELSIF v_post.operation_id=(p_authority).event_id
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
END
$function$;
CREATE FUNCTION claimcore_witness.verify_prune_targets(p_identity claimcore_witness.installation_identity, p_source claimcore_witness.prune_source, v_existing claimcore_witness.journal)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_target claimcore_witness.journal%ROWTYPE;
    v_digest bytea;
    v_count bigint := 0;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    v_digest := pg_catalog.sha256(
        pg_catalog.convert_to('claimcore:witness-prune-targets:v1', 'UTF8')
        || pg_catalog.decode('00', 'hex'));
    FOR v_target IN SELECT j.* FROM claimcore_witness.journal j
        WHERE j.installation_id=(p_identity).installation_id AND j.scope_kind='CASE'
          AND j.subject_case_id=(p_source).case_id AND j.sequence<=((p_source).cutoff).sequence
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
              AND p.sequence=v_target.sequence AND p.subject_case_id=(p_source).case_id
              AND pg_catalog.sha256(p.encrypted_payload)=v_target.payload_sha256
        ) THEN
            RAISE EXCEPTION 'witness prune payload state is inconsistent';
        END IF;
    END LOOP;
    IF v_count <> (p_source).target_count OR v_digest <> (p_source).target_digest THEN
        RAISE EXCEPTION 'witness prune target set diverged';
    END IF;

    -- A replay checks the sealed historical phase set and physical absence above, then returns
    -- the exact settled ticket. Later legitimate CASE control events and key rotation cannot
    -- rebase this irreversible event; a fresh settlement still requires the current key.
END
$function$;
