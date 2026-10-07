CREATE FUNCTION claimcore_witness.validate_loss_retirement_intent(p_identity claimcore_witness.installation_identity, p_previous claimcore_witness.journal_tip, p_plan claimcore_witness.loss_retirement_plan, p_approvals claimcore_witness.owner_signature_pair, p_payload claimcore_witness.journal_payload)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    PERFORM claimcore_witness.validate_installation_identity(p_identity);
    PERFORM claimcore_witness.validate_journal_tip(p_previous);
    PERFORM claimcore_witness.validate_loss_retirement_plan(p_plan);
    PERFORM claimcore_witness.validate_owner_signature_pair(p_approvals);
    PERFORM claimcore_witness.validate_journal_payload(p_payload);
    IF session_user <> 'claimcore_witness_owner'
       OR (p_identity).installation_id IS NULL OR (p_identity).lineage_id IS NULL OR (p_identity).epoch IS NULL OR (p_identity).epoch < 1
       OR (p_plan).retirement_id IS NULL OR (p_plan).retirement_id='00000000-0000-0000-0000-000000000000'::uuid
       OR (p_previous).sequence IS NULL OR (p_previous).sequence < 0
       OR (p_previous).entry_hash IS NULL OR octet_length((p_previous).entry_hash) <> 32
       OR (p_plan).canonical IS NULL OR octet_length((p_plan).canonical) NOT BETWEEN 1 AND 16384
       OR ((p_approvals).first).signature IS NULL OR octet_length(((p_approvals).first).signature) <> 64
       OR ((p_approvals).second).signature IS NULL OR octet_length(((p_approvals).second).signature) <> 64
       OR ((p_approvals).first).signer_id IS NULL OR ((p_approvals).second).signer_id IS NULL OR ((p_approvals).first).signer_id=((p_approvals).second).signer_id
       OR ((p_approvals).first).actor_id IS NULL OR ((p_approvals).second).actor_id IS NULL
       OR ((p_approvals).first).actor_id=((p_approvals).second).actor_id
       OR (p_plan).operation_set_kind NOT IN ('KNOWN_OPERATIONS','UNKNOWN_OPERATIONS')
       OR (p_plan).known_operation_count IS NULL OR (p_plan).known_operation_count NOT BETWEEN 0 AND 10000
       OR ((p_plan).operation_set_kind='UNKNOWN_OPERATIONS' AND (p_plan).known_operation_count<>0)
       OR (p_plan).known_operation_digest IS NULL OR octet_length((p_plan).known_operation_digest)<>32
       OR (p_payload).key_id IS NULL OR (p_payload).encrypted_payload IS NULL
       OR octet_length((p_payload).encrypted_payload) NOT BETWEEN 1 AND 1048576
    THEN
        RAISE EXCEPTION 'invalid installation loss retirement intent';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.verify_loss_retirement_identity(p_identity claimcore_witness.installation_identity, p_payload claimcore_witness.journal_payload)
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
    IF v_installation.installation_id<>(p_identity).installation_id
       OR v_installation.lineage_id<>(p_identity).lineage_id
       OR v_installation.epoch<>(p_identity).epoch
       OR v_installation.active_key_id<>(p_payload).key_id THEN
        RAISE EXCEPTION 'installation loss identity diverged';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.verify_loss_retirement_retry(p_identity claimcore_witness.installation_identity, p_previous claimcore_witness.journal_tip, p_plan claimcore_witness.loss_retirement_plan, p_approvals claimcore_witness.owner_signature_pair, p_payload claimcore_witness.journal_payload)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_existing claimcore_witness.installation_loss_retirements%ROWTYPE;
    v_digest bytea;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    SELECT * INTO STRICT v_installation FROM claimcore_witness.installation WHERE singleton;
    SELECT * INTO STRICT v_existing FROM claimcore_witness.installation_loss_retirements WHERE retirement_id=(p_plan).retirement_id;
    v_digest:=pg_catalog.sha256((p_payload).encrypted_payload);
        IF v_installation.loss_retirement_id<>(p_plan).retirement_id
           OR NOT (v_installation.loss_retirement_pending OR v_installation.loss_retired)
           OR v_installation.loss_retirement_intent_sequence<>v_existing.intent_sequence
           OR v_installation.loss_retirement_intent_hash<>v_existing.intent_hash
           OR v_existing.installation_id<>(p_identity).installation_id
           OR v_existing.lineage_id<>(p_identity).lineage_id
           OR v_existing.old_epoch<>(p_identity).epoch
           OR v_existing.previous_sequence<>(p_previous).sequence
           OR v_existing.previous_hash<>(p_previous).entry_hash
           OR v_existing.canonical_decision<>(p_plan).canonical
           OR v_existing.signature_one<>((p_approvals).first).signature
           OR v_existing.signature_two<>((p_approvals).second).signature
           OR v_existing.signer_one_id<>((p_approvals).first).signer_id
           OR v_existing.signer_two_id<>((p_approvals).second).signer_id
           OR v_existing.owner_one_actor_id<>((p_approvals).first).actor_id
           OR v_existing.owner_two_actor_id<>((p_approvals).second).actor_id
           OR v_existing.operation_set_kind<>(p_plan).operation_set_kind
           OR v_existing.known_operation_count<>(p_plan).known_operation_count
           OR v_existing.known_operation_digest<>(p_plan).known_operation_digest
           OR NOT EXISTS (
               SELECT 1 FROM claimcore_witness.journal j
               JOIN claimcore_witness.journal_payloads p
                 ON p.installation_id=j.installation_id AND p.sequence=j.sequence
               WHERE j.installation_id=(p_identity).installation_id AND j.operation_id=(p_plan).retirement_id
                 AND j.phase='INTENT' AND j.sequence=v_existing.intent_sequence
                 AND j.entry_hash=v_existing.intent_hash AND j.payload_sha256=v_digest
                 AND p.encrypted_payload=(p_payload).encrypted_payload
           ) THEN
            RAISE EXCEPTION 'divergent installation loss retirement retry';
        END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.require_loss_retirement_cutoff(p_identity claimcore_witness.installation_identity, p_previous claimcore_witness.journal_tip, p_plan claimcore_witness.loss_retirement_plan)
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
    IF v_installation.loss_retirement_pending OR v_installation.loss_retired
       OR v_installation.tip_sequence<>(p_previous).sequence
       OR v_installation.tip_hash<>(p_previous).entry_hash
       OR EXISTS (SELECT 1 FROM claimcore_witness.journal
                  WHERE installation_id=(p_identity).installation_id AND operation_id=(p_plan).retirement_id) THEN
        RAISE EXCEPTION 'installation loss retirement cutoff diverged';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.prepare_installation_loss_retirement(p_identity claimcore_witness.installation_identity, p_previous claimcore_witness.journal_tip, p_plan claimcore_witness.loss_retirement_plan, p_approvals claimcore_witness.owner_signature_pair, p_payload claimcore_witness.journal_payload)
RETURNS TABLE(sequence bigint,entry_hash bytea,payload_sha256 bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_existing claimcore_witness.installation_loss_retirements%ROWTYPE;
    v_digest bytea;
BEGIN
    PERFORM claimcore_witness.validate_loss_retirement_intent(p_identity,p_previous,p_plan,p_approvals,p_payload);
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    PERFORM claimcore_witness.verify_loss_retirement_identity(p_identity,p_payload);
    v_digest:=pg_catalog.sha256((p_payload).encrypted_payload);
    SELECT * INTO v_existing FROM claimcore_witness.installation_loss_retirements
      WHERE retirement_id=(p_plan).retirement_id;
    IF FOUND THEN
    PERFORM claimcore_witness.verify_loss_retirement_retry(p_identity,p_previous,p_plan,p_approvals,p_payload);
        sequence:=v_existing.intent_sequence;
        entry_hash:=v_existing.intent_hash;
        payload_sha256:=v_digest;
        RETURN NEXT;
        RETURN;
    END IF;
    PERFORM claimcore_witness.require_loss_retirement_cutoff(p_identity,p_previous,p_plan);
    SELECT inserted.sequence,inserted.entry_hash,inserted.payload_sha256
      INTO sequence,entry_hash,v_digest FROM claimcore_witness.insert_journal_entry(p_identity,ROW((p_previous).sequence,(p_previous).entry_hash)::claimcore_witness.journal_tip,ROW('INSTALLATION',NULL)::claimcore_witness.journal_subject,ROW((p_plan).retirement_id,'INTENT',(p_payload).key_id,(p_payload).encrypted_payload)::claimcore_witness.journal_request) inserted;
    INSERT INTO claimcore_witness.installation_loss_retirements
      (retirement_id,installation_id,lineage_id,old_epoch,previous_sequence,previous_hash,
       canonical_decision,canonical_sha256,signature_one,signature_two,signer_one_id,
       signer_two_id,owner_one_actor_id,owner_two_actor_id,operation_set_kind,
       known_operation_count,known_operation_digest,intent_sequence,intent_hash)
    VALUES ((p_plan).retirement_id,(p_identity).installation_id,(p_identity).lineage_id,(p_identity).epoch,(p_previous).sequence,
            (p_previous).entry_hash,(p_plan).canonical,pg_catalog.sha256((p_plan).canonical),((p_approvals).first).signature,
            ((p_approvals).second).signature,((p_approvals).first).signer_id,((p_approvals).second).signer_id,((p_approvals).first).actor_id,
            ((p_approvals).second).actor_id,(p_plan).operation_set_kind,(p_plan).known_operation_count,
            (p_plan).known_operation_digest,sequence,entry_hash);
    UPDATE claimcore_witness.installation SET
        loss_retirement_pending=true,loss_retirement_id=(p_plan).retirement_id,
        loss_retirement_intent_sequence=sequence,loss_retirement_intent_hash=entry_hash,
        tip_sequence=sequence,tip_hash=entry_hash WHERE singleton;
    payload_sha256:=v_digest;
    RETURN NEXT;
END

$function$;
