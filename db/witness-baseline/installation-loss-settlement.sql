CREATE FUNCTION claimcore_witness.validate_loss_retirement_settlement(p_identity claimcore_witness.installation_identity, p_proof claimcore_witness.loss_settlement_proof, p_payload claimcore_witness.journal_payload)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    PERFORM claimcore_witness.validate_installation_identity(p_identity);
    PERFORM claimcore_witness.validate_loss_settlement_proof(p_proof);
    PERFORM claimcore_witness.validate_journal_payload(p_payload);
    IF session_user <> 'claimcore_witness_owner'
       OR (p_identity).installation_id IS NULL OR (p_identity).lineage_id IS NULL OR (p_identity).epoch IS NULL
       OR ((p_proof).intent).operation_id IS NULL OR ((p_proof).intent).sequence IS NULL OR ((p_proof).intent).sequence<1
       OR ((p_proof).intent).entry_hash IS NULL OR octet_length(((p_proof).intent).entry_hash)<>32
       OR (p_proof).primary_candidate_sha256 IS NULL OR octet_length((p_proof).primary_candidate_sha256)<>32
       OR (p_payload).key_id IS NULL OR (p_payload).encrypted_payload IS NULL
       OR octet_length((p_payload).encrypted_payload) NOT BETWEEN 1 AND 1048576 THEN
        RAISE EXCEPTION 'invalid installation loss retirement settlement';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.verify_loss_retirement_evidence(p_identity claimcore_witness.installation_identity, p_proof claimcore_witness.loss_settlement_proof, p_payload claimcore_witness.journal_payload)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_retirement claimcore_witness.installation_loss_retirements%ROWTYPE;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    SELECT * INTO STRICT v_installation FROM claimcore_witness.installation WHERE singleton;
    SELECT * INTO STRICT v_retirement FROM claimcore_witness.installation_loss_retirements WHERE retirement_id=((p_proof).intent).operation_id;
    IF v_installation.installation_id<>(p_identity).installation_id
       OR v_installation.lineage_id<>(p_identity).lineage_id
       OR v_installation.epoch<>(p_identity).epoch OR v_installation.active_key_id<>(p_payload).key_id
       OR v_retirement.intent_sequence<>((p_proof).intent).sequence
       OR v_retirement.intent_hash<>((p_proof).intent).entry_hash
       OR v_retirement.canonical_sha256<>(p_proof).primary_candidate_sha256 THEN
        RAISE EXCEPTION 'installation loss retirement evidence diverged';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.verify_loss_retirement_settlement_retry(p_identity claimcore_witness.installation_identity, p_proof claimcore_witness.loss_settlement_proof, p_payload claimcore_witness.journal_payload)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_retirement claimcore_witness.installation_loss_retirements%ROWTYPE;
    v_digest bytea;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    SELECT * INTO STRICT v_installation FROM claimcore_witness.installation WHERE singleton;
    SELECT * INTO STRICT v_retirement FROM claimcore_witness.installation_loss_retirements WHERE retirement_id=((p_proof).intent).operation_id;
    v_digest:=pg_catalog.sha256((p_payload).encrypted_payload);
        IF NOT v_installation.loss_retired OR v_installation.loss_retirement_pending
           OR v_installation.loss_retirement_id<>((p_proof).intent).operation_id
           OR v_installation.loss_retirement_sequence<>v_retirement.settlement_sequence
           OR v_installation.loss_retirement_hash<>v_retirement.settlement_hash
           OR NOT EXISTS (
               SELECT 1 FROM claimcore_witness.journal j
               JOIN claimcore_witness.journal_payloads p
                 ON p.installation_id=j.installation_id AND p.sequence=j.sequence
               WHERE j.installation_id=(p_identity).installation_id AND j.operation_id=((p_proof).intent).operation_id
                 AND j.phase='SETTLED_AUTHORITY'
                 AND j.sequence=v_retirement.settlement_sequence
                 AND j.entry_hash=v_retirement.settlement_hash
                 AND j.payload_sha256=v_digest AND p.encrypted_payload=(p_payload).encrypted_payload
           ) THEN
            RAISE EXCEPTION 'divergent installation loss retirement settlement retry';
        END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.require_pending_loss_retirement(p_proof claimcore_witness.loss_settlement_proof)
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
    IF NOT v_installation.loss_retirement_pending OR v_installation.loss_retired
       OR v_installation.loss_retirement_id<>((p_proof).intent).operation_id
       OR v_installation.tip_sequence<>((p_proof).intent).sequence
       OR v_installation.tip_hash<>((p_proof).intent).entry_hash THEN
        RAISE EXCEPTION 'installation loss retirement is not pending';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.settle_installation_loss_retirement(p_identity claimcore_witness.installation_identity, p_proof claimcore_witness.loss_settlement_proof, p_payload claimcore_witness.journal_payload)
RETURNS TABLE(sequence bigint,entry_hash bytea,payload_sha256 bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_retirement claimcore_witness.installation_loss_retirements%ROWTYPE;
    v_digest bytea;
BEGIN
    PERFORM claimcore_witness.validate_loss_retirement_settlement(p_identity,p_proof,p_payload);
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    SELECT * INTO STRICT v_retirement
      FROM claimcore_witness.installation_loss_retirements WHERE retirement_id=((p_proof).intent).operation_id FOR UPDATE;
    PERFORM claimcore_witness.verify_loss_retirement_evidence(p_identity,p_proof,p_payload);
    v_digest:=pg_catalog.sha256((p_payload).encrypted_payload);
    IF v_retirement.settlement_sequence IS NOT NULL THEN
    PERFORM claimcore_witness.verify_loss_retirement_settlement_retry(p_identity,p_proof,p_payload);
        sequence:=v_retirement.settlement_sequence;
        entry_hash:=v_retirement.settlement_hash;
        payload_sha256:=v_digest;
        RETURN NEXT;
        RETURN;
    END IF;
    PERFORM claimcore_witness.require_pending_loss_retirement(p_proof);
    SELECT inserted.sequence,inserted.entry_hash,inserted.payload_sha256
      INTO sequence,entry_hash,v_digest FROM claimcore_witness.insert_journal_entry(p_identity,ROW(((p_proof).intent).sequence,((p_proof).intent).entry_hash)::claimcore_witness.journal_tip,ROW('INSTALLATION',NULL)::claimcore_witness.journal_subject,ROW(((p_proof).intent).operation_id,'SETTLED_AUTHORITY',(p_payload).key_id,(p_payload).encrypted_payload)::claimcore_witness.journal_request) inserted;
    UPDATE claimcore_witness.installation_loss_retirements SET
        settlement_sequence=sequence,settlement_hash=entry_hash
      WHERE retirement_id=((p_proof).intent).operation_id;
    UPDATE claimcore_witness.installation SET
        loss_retirement_pending=false,loss_retired=true,
        loss_retirement_sequence=sequence,loss_retirement_hash=entry_hash,
        tip_sequence=sequence,tip_hash=entry_hash WHERE singleton;
    payload_sha256:=v_digest;
    RETURN NEXT;
END

$function$;
