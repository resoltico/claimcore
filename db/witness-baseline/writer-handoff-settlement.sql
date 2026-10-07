CREATE FUNCTION claimcore_witness.validate_handoff_settlement(p_identity claimcore_witness.installation_identity, p_prepared claimcore_witness.journal_ticket, p_capabilities claimcore_witness.writer_capabilities, p_signed claimcore_witness.signed_candidate, p_payload claimcore_witness.journal_payload)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    PERFORM claimcore_witness.validate_installation_identity(p_identity);
    PERFORM claimcore_witness.validate_journal_ticket(p_prepared);
    PERFORM claimcore_witness.validate_writer_capabilities(p_capabilities);
    PERFORM claimcore_witness.validate_signed_candidate(p_signed);
    PERFORM claimcore_witness.validate_journal_payload(p_payload);
    IF session_user <> 'claimcore_witness_owner'
       OR (p_prepared).operation_id IS NULL OR (p_prepared).sequence IS NULL OR (p_prepared).sequence < 1
       OR (p_prepared).entry_hash IS NULL OR octet_length((p_prepared).entry_hash) <> 32
       OR (p_capabilities).old_capability IS NULL OR octet_length((p_capabilities).old_capability) <> 32
       OR (p_capabilities).new_capability IS NULL OR octet_length((p_capabilities).new_capability) <> 32
       OR (p_signed).canonical IS NULL
       OR octet_length((p_signed).canonical) NOT BETWEEN 1 AND 16384
       OR (p_signed).signature IS NULL OR octet_length((p_signed).signature) <> 64
       OR (p_payload).key_id IS NULL OR (p_payload).encrypted_payload IS NULL
       OR octet_length((p_payload).encrypted_payload) NOT BETWEEN 1 AND 1048576
    THEN
        RAISE EXCEPTION 'invalid writer handoff settlement';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.verify_handoff_settlement_intent(p_identity claimcore_witness.installation_identity, p_prepared claimcore_witness.journal_ticket, p_capabilities claimcore_witness.writer_capabilities, p_payload claimcore_witness.journal_payload)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_handoff claimcore_witness.writer_handoffs%ROWTYPE;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    SELECT * INTO STRICT v_installation FROM claimcore_witness.installation WHERE singleton;
    SELECT * INTO STRICT v_handoff FROM claimcore_witness.writer_handoffs h WHERE h.handoff_id=(p_prepared).operation_id;
    IF v_handoff.prepare_sequence <> (p_prepared).sequence
       OR v_handoff.prepare_hash <> (p_prepared).entry_hash
       OR v_handoff.new_capability_sha256 <> pg_catalog.sha256((p_capabilities).new_capability)
       OR v_handoff.abort_sequence IS NOT NULL
       OR v_installation.installation_id <> (p_identity).installation_id
       OR v_installation.lineage_id <> (p_identity).lineage_id OR v_installation.epoch <> (p_identity).epoch
       OR v_installation.active_key_id <> (p_payload).key_id
       OR NOT EXISTS (
           SELECT 1 FROM claimcore_witness.journal i
           WHERE i.installation_id=(p_identity).installation_id AND i.operation_id=(p_prepared).operation_id
             AND i.phase='INTENT' AND i.sequence=(p_prepared).sequence
             AND i.entry_hash=(p_prepared).entry_hash AND i.scope_kind='INSTALLATION'
       ) THEN
        RAISE EXCEPTION 'writer handoff intent diverged';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.verify_handoff_settlement_retry(p_identity claimcore_witness.installation_identity, p_prepared claimcore_witness.journal_ticket, p_signed claimcore_witness.signed_candidate, p_payload claimcore_witness.journal_payload)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_handoff claimcore_witness.writer_handoffs%ROWTYPE;
    v_digest bytea;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    SELECT * INTO STRICT v_installation FROM claimcore_witness.installation WHERE singleton;
    SELECT * INTO STRICT v_handoff FROM claimcore_witness.writer_handoffs h WHERE h.handoff_id=(p_prepared).operation_id;
    v_digest:=pg_catalog.sha256((p_payload).encrypted_payload);
        IF v_installation.handoff_pending
           OR v_installation.writer_generation <> v_handoff.new_generation
           OR v_installation.writer_capability_sha256 <> v_handoff.new_capability_sha256
           OR v_handoff.settlement_canonical <> (p_signed).canonical
           OR v_handoff.settlement_signature <> (p_signed).signature
           OR NOT EXISTS (
               SELECT 1 FROM claimcore_witness.journal j
               JOIN claimcore_witness.journal_payloads p
                 ON p.installation_id=j.installation_id AND p.sequence=j.sequence
               WHERE j.installation_id=(p_identity).installation_id AND j.operation_id=(p_prepared).operation_id
                 AND j.phase='SETTLED_AUTHORITY'
                 AND j.sequence=v_handoff.settlement_sequence
                 AND j.entry_hash=v_handoff.settlement_hash
                 AND j.payload_sha256=v_digest
                 AND p.encrypted_payload=(p_payload).encrypted_payload
           ) THEN
            RAISE EXCEPTION 'divergent writer handoff retry';
        END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.require_handoff_settlement(p_identity claimcore_witness.installation_identity, p_prepared claimcore_witness.journal_ticket, p_capabilities claimcore_witness.writer_capabilities)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_handoff claimcore_witness.writer_handoffs%ROWTYPE;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    SELECT * INTO STRICT v_installation FROM claimcore_witness.installation WHERE singleton;
    SELECT * INTO STRICT v_handoff FROM claimcore_witness.writer_handoffs h WHERE h.handoff_id=(p_prepared).operation_id;
    IF NOT v_installation.handoff_pending
       OR v_installation.writer_generation <> v_handoff.old_generation
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256((p_capabilities).old_capability)
       OR v_installation.tip_sequence <> (p_prepared).sequence
       OR v_installation.tip_hash <> (p_prepared).entry_hash
       OR EXISTS (
           SELECT 1 FROM claimcore_witness.journal i
           WHERE i.installation_id=(p_identity).installation_id AND i.phase='INTENT'
             AND i.operation_id<>(p_prepared).operation_id
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
END
$function$;
CREATE FUNCTION claimcore_witness.record_handoff_settlement(p_prepared claimcore_witness.journal_ticket, p_signed claimcore_witness.signed_candidate, v_sequence bigint, v_hash bytea)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    UPDATE claimcore_witness.writer_handoffs
       SET settlement_candidate_sha256=pg_catalog.sha256((p_signed).canonical),
           settlement_sequence=v_sequence,settlement_hash=v_hash,
           settlement_canonical=(p_signed).canonical,
           settlement_signature=(p_signed).signature
      WHERE handoff_id=(p_prepared).operation_id;
END
$function$;
CREATE FUNCTION claimcore_witness.commit_writer_handoff(p_identity claimcore_witness.installation_identity, p_prepared claimcore_witness.journal_ticket, p_capabilities claimcore_witness.writer_capabilities, p_signed claimcore_witness.signed_candidate, p_payload claimcore_witness.journal_payload)
RETURNS TABLE(sequence bigint,entry_hash bytea,payload_sha256 bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_handoff claimcore_witness.writer_handoffs%ROWTYPE;
    v_digest bytea;
    v_sequence bigint;
    v_hash bytea;
BEGIN
    PERFORM claimcore_witness.validate_handoff_settlement(p_identity,p_prepared,p_capabilities,p_signed,p_payload);
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    SELECT * INTO STRICT v_handoff FROM claimcore_witness.writer_handoffs h
      WHERE h.handoff_id=(p_prepared).operation_id FOR UPDATE;
    v_digest := pg_catalog.sha256((p_payload).encrypted_payload);
    PERFORM claimcore_witness.verify_handoff_settlement_intent(p_identity,p_prepared,p_capabilities,p_payload);
    IF v_handoff.settlement_sequence IS NOT NULL THEN
    PERFORM claimcore_witness.verify_handoff_settlement_retry(p_identity,p_prepared,p_signed,p_payload);
        sequence := v_handoff.settlement_sequence;
        entry_hash := v_handoff.settlement_hash;
        payload_sha256 := v_digest;
        RETURN NEXT;
        RETURN;
    END IF;
    PERFORM claimcore_witness.require_handoff_settlement(p_identity,p_prepared,p_capabilities);
    SELECT inserted.sequence,inserted.entry_hash,inserted.payload_sha256
      INTO v_sequence,v_hash,v_digest FROM claimcore_witness.insert_journal_entry(p_identity,ROW((p_prepared).sequence,(p_prepared).entry_hash)::claimcore_witness.journal_tip,ROW('INSTALLATION',NULL)::claimcore_witness.journal_subject,ROW((p_prepared).operation_id,'SETTLED_AUTHORITY',(p_payload).key_id,(p_payload).encrypted_payload)::claimcore_witness.journal_request) inserted;
    PERFORM claimcore_witness.record_handoff_settlement(p_prepared,p_signed,v_sequence,v_hash);
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

$function$;
