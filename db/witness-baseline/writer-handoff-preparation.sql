CREATE FUNCTION claimcore_witness.validate_handoff_preparation(p_identity claimcore_witness.installation_identity, p_plan claimcore_witness.handoff_plan, p_previous claimcore_witness.journal_tip, p_approval claimcore_witness.handoff_approval, p_delivery claimcore_witness.writer_delivery)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    PERFORM claimcore_witness.validate_installation_identity(p_identity);
    PERFORM claimcore_witness.validate_handoff_plan(p_plan);
    PERFORM claimcore_witness.validate_journal_tip(p_previous);
    PERFORM claimcore_witness.validate_handoff_approval(p_approval);
    PERFORM claimcore_witness.validate_writer_delivery(p_delivery);
    IF session_user <> 'claimcore_witness_owner'
       OR (p_plan).handoff_id IS NULL OR (p_plan).handoff_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR (p_plan).old_generation IS NULL OR (p_plan).old_generation < 1
       OR (p_previous).sequence IS NULL OR (p_previous).sequence < 0
       OR (p_previous).entry_hash IS NULL OR octet_length((p_previous).entry_hash) <> 32
       OR (p_plan).new_capability_sha256 IS NULL OR octet_length((p_plan).new_capability_sha256) <> 32
       OR (p_approval).signing_key_id IS NULL
       OR (p_approval).canonical IS NULL OR octet_length((p_approval).canonical) NOT BETWEEN 1 AND 16384
       OR (p_approval).signature IS NULL OR octet_length((p_approval).signature) <> 64
       OR (p_approval).approval_one_id IS NULL OR (p_approval).approval_two_id IS NULL
       OR (p_approval).approval_one_id = (p_approval).approval_two_id
       OR (p_delivery).key_id IS NULL OR (p_delivery).encrypted_payload IS NULL
       OR octet_length((p_delivery).encrypted_payload) NOT BETWEEN 1 AND 1048576
       OR (p_delivery).writer_capability IS NULL OR octet_length((p_delivery).writer_capability) <> 32
    THEN
        RAISE EXCEPTION 'invalid writer handoff preparation';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.require_handoff_preparation(p_identity claimcore_witness.installation_identity, p_plan claimcore_witness.handoff_plan, p_previous claimcore_witness.journal_tip, p_delivery claimcore_witness.writer_delivery)
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
       OR v_installation.lineage_id <> (p_identity).lineage_id OR v_installation.epoch <> (p_identity).epoch
       OR v_installation.writer_generation <> (p_plan).old_generation
       OR v_installation.handoff_pending OR v_installation.activation_pending
       OR v_installation.tip_sequence <> (p_previous).sequence
       OR v_installation.tip_hash <> (p_previous).entry_hash
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256((p_delivery).writer_capability)
       OR v_installation.writer_capability_sha256 = (p_plan).new_capability_sha256
       OR EXISTS (
           SELECT 1 FROM claimcore_witness.journal i
           WHERE i.installation_id=(p_identity).installation_id AND i.phase='INTENT'
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
END
$function$;
CREATE FUNCTION claimcore_witness.verify_handoff_preparation_retry(p_identity claimcore_witness.installation_identity, p_plan claimcore_witness.handoff_plan, p_previous claimcore_witness.journal_tip, p_approval claimcore_witness.handoff_approval)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_existing claimcore_witness.writer_handoffs%ROWTYPE;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    SELECT * INTO STRICT v_installation FROM claimcore_witness.installation WHERE singleton;
    SELECT * INTO STRICT v_existing FROM claimcore_witness.writer_handoffs h WHERE h.handoff_id=(p_plan).handoff_id;
        IF v_installation.installation_id<>(p_identity).installation_id
           OR v_installation.lineage_id<>(p_identity).lineage_id
           OR v_installation.epoch<>(p_identity).epoch
           OR NOT v_installation.handoff_pending
           OR v_existing.old_generation <> (p_plan).old_generation
           OR v_existing.prepare_canonical <> (p_approval).canonical
           OR v_existing.prepare_signature <> (p_approval).signature
           OR v_existing.previous_sequence <> (p_previous).sequence
           OR v_existing.previous_hash <> (p_previous).entry_hash
           OR v_existing.new_capability_sha256 <> (p_plan).new_capability_sha256
           OR v_existing.settlement_sequence IS NOT NULL
           OR v_existing.abort_sequence IS NOT NULL THEN
            RAISE EXCEPTION 'divergent writer handoff preparation';
        END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.prepare_writer_handoff(p_identity claimcore_witness.installation_identity, p_plan claimcore_witness.handoff_plan, p_previous claimcore_witness.journal_tip, p_approval claimcore_witness.handoff_approval, p_delivery claimcore_witness.writer_delivery)
RETURNS TABLE(sequence bigint,entry_hash bytea,payload_sha256 bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_existing claimcore_witness.writer_handoffs%ROWTYPE;
    v_sequence bigint;
    v_hash bytea;
    v_payload_hash bytea;
BEGIN
    PERFORM claimcore_witness.validate_handoff_preparation(p_identity,p_plan,p_previous,p_approval,p_delivery);
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    SELECT * INTO v_existing FROM claimcore_witness.writer_handoffs h
      WHERE h.handoff_id=(p_plan).handoff_id;
    IF FOUND THEN
    PERFORM claimcore_witness.verify_handoff_preparation_retry(p_identity,p_plan,p_previous,p_approval);
        sequence := v_existing.prepare_sequence;
        entry_hash := v_existing.prepare_hash;
        SELECT j.payload_sha256 INTO payload_sha256 FROM claimcore_witness.journal j
          WHERE j.installation_id=(p_identity).installation_id AND j.sequence=v_existing.prepare_sequence;
        RETURN NEXT;
        RETURN;
    END IF;
    PERFORM claimcore_witness.require_handoff_preparation(p_identity,p_plan,p_previous,p_delivery);
    SELECT appended.sequence, appended.entry_hash, appended.payload_sha256
      INTO v_sequence, v_hash, v_payload_hash
      FROM claimcore_witness.append(ROW((p_identity).installation_id,(p_identity).lineage_id,(p_identity).epoch)::claimcore_witness.installation_identity,ROW('INSTALLATION',NULL)::claimcore_witness.journal_subject,ROW((p_plan).handoff_id,'INTENT',(p_delivery).key_id,(p_delivery).encrypted_payload)::claimcore_witness.journal_request,(p_delivery).writer_capability) AS appended;
    INSERT INTO claimcore_witness.writer_handoffs
      (handoff_id,old_generation,new_generation,previous_sequence,previous_hash,
       new_capability_sha256,checkpoint_signing_key_id,prepare_canonical,
       prepare_signature,approval_one_id,approval_two_id,prepare_candidate_sha256,
       prepare_sequence,prepare_hash)
    VALUES
      ((p_plan).handoff_id,(p_plan).old_generation,(p_plan).old_generation+1,(p_previous).sequence,(p_previous).entry_hash,
       (p_plan).new_capability_sha256,(p_approval).signing_key_id,(p_approval).canonical,
       (p_approval).signature,(p_approval).approval_one_id,(p_approval).approval_two_id,
       pg_catalog.sha256((p_approval).canonical),v_sequence,v_hash);
    UPDATE claimcore_witness.installation SET handoff_pending=true WHERE singleton;
    sequence := v_sequence;
    entry_hash := v_hash;
    payload_sha256 := v_payload_hash;
    RETURN NEXT;
END

$function$;
