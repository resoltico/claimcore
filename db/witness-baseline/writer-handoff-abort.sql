CREATE FUNCTION claimcore_witness.validate_handoff_abort(p_identity claimcore_witness.installation_identity, p_prepared claimcore_witness.journal_ticket, p_decision claimcore_witness.handoff_abort_decision, p_delivery claimcore_witness.writer_delivery)
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
    PERFORM claimcore_witness.validate_handoff_abort_decision(p_decision);
    PERFORM claimcore_witness.validate_writer_delivery(p_delivery);
    IF session_user <> 'claimcore_witness_owner'
       OR (p_prepared).operation_id IS NULL OR (p_prepared).operation_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR (p_prepared).sequence IS NULL OR (p_prepared).sequence < 1
       OR (p_prepared).entry_hash IS NULL OR octet_length((p_prepared).entry_hash) <> 32
       OR (p_delivery).writer_capability IS NULL OR octet_length((p_delivery).writer_capability) <> 32
       OR (p_decision).canonical IS NULL OR octet_length((p_decision).canonical) NOT BETWEEN 1 AND 16384
       OR (p_decision).signature_one IS NULL OR octet_length((p_decision).signature_one) <> 64
       OR (p_decision).signature_two IS NULL OR octet_length((p_decision).signature_two) <> 64
       OR (p_decision).signing_key_one IS NULL OR (p_decision).signing_key_two IS NULL
       OR (p_decision).signing_key_one = (p_decision).signing_key_two
       OR (p_delivery).key_id IS NULL OR (p_delivery).encrypted_payload IS NULL
       OR octet_length((p_delivery).encrypted_payload) NOT BETWEEN 1 AND 1048576
    THEN
        RAISE EXCEPTION 'invalid writer handoff abort';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.verify_handoff_abort_source(p_identity claimcore_witness.installation_identity, p_prepared claimcore_witness.journal_ticket, p_delivery claimcore_witness.writer_delivery)
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
    IF v_installation.installation_id <> (p_identity).installation_id
       OR v_installation.lineage_id <> (p_identity).lineage_id OR v_installation.epoch <> (p_identity).epoch
       OR v_installation.active_key_id <> (p_delivery).key_id
       OR v_handoff.prepare_sequence <> (p_prepared).sequence
       OR v_handoff.prepare_hash <> (p_prepared).entry_hash
       OR v_handoff.settlement_sequence IS NOT NULL
       OR v_installation.writer_generation <> v_handoff.old_generation
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256((p_delivery).writer_capability)
       OR NOT EXISTS (
           SELECT 1 FROM claimcore_witness.journal j
           WHERE j.installation_id=(p_identity).installation_id AND j.operation_id=(p_prepared).operation_id
             AND j.phase='INTENT' AND j.sequence=(p_prepared).sequence
             AND j.entry_hash=(p_prepared).entry_hash AND j.scope_kind='INSTALLATION'
       ) THEN
        RAISE EXCEPTION 'writer handoff abort prerequisites diverged';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.verify_handoff_abort_retry(p_identity claimcore_witness.installation_identity, p_prepared claimcore_witness.journal_ticket, p_decision claimcore_witness.handoff_abort_decision, p_delivery claimcore_witness.writer_delivery)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_handoff claimcore_witness.writer_handoffs%ROWTYPE;
    v_payload_hash bytea;
    v_candidate_hash bytea;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    SELECT * INTO STRICT v_installation FROM claimcore_witness.installation WHERE singleton;
    SELECT * INTO STRICT v_handoff FROM claimcore_witness.writer_handoffs h WHERE h.handoff_id=(p_prepared).operation_id;
    v_candidate_hash:=pg_catalog.sha256(pg_catalog.convert_to('CLAIMCORE_WRITER_HANDOFF_ABORT_V1:', 'UTF8') || (p_decision).canonical || (p_decision).signature_one || (p_decision).signature_two);
    v_payload_hash:=pg_catalog.sha256((p_delivery).encrypted_payload);
        IF v_handoff.abort_canonical <> (p_decision).canonical
           OR v_handoff.abort_signature_one <> (p_decision).signature_one
           OR v_handoff.abort_signature_two <> (p_decision).signature_two
           OR v_handoff.abort_signing_key_one <> (p_decision).signing_key_one
           OR v_handoff.abort_signing_key_two <> (p_decision).signing_key_two
           OR v_handoff.abort_candidate_sha256 <> v_candidate_hash
           OR NOT EXISTS (
               SELECT 1 FROM claimcore_witness.journal j
               JOIN claimcore_witness.journal_payloads p
                 ON p.installation_id=j.installation_id AND p.sequence=j.sequence
               WHERE j.installation_id=(p_identity).installation_id AND j.operation_id=(p_prepared).operation_id
                 AND j.phase='ABORTED_BEFORE_COMMIT'
                 AND j.sequence=v_handoff.abort_sequence
                 AND j.entry_hash=v_handoff.abort_hash
                 AND j.payload_sha256=v_payload_hash
                 AND p.encrypted_payload=(p_delivery).encrypted_payload
           ) THEN
            RAISE EXCEPTION 'divergent writer handoff abort retry';
        END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.require_handoff_abort(p_prepared claimcore_witness.journal_ticket)
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
    IF NOT v_installation.handoff_pending
       OR v_installation.tip_sequence <> (p_prepared).sequence
       OR v_installation.tip_hash <> (p_prepared).entry_hash THEN
        RAISE EXCEPTION 'writer handoff abort tip diverged';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.record_handoff_abort(p_prepared claimcore_witness.journal_ticket, p_decision claimcore_witness.handoff_abort_decision, v_candidate_hash bytea, v_sequence bigint, v_hash bytea)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    UPDATE claimcore_witness.writer_handoffs
       SET abort_candidate_sha256=v_candidate_hash,abort_sequence=v_sequence,
           abort_hash=v_hash,abort_canonical=(p_decision).canonical,
           abort_signature_one=(p_decision).signature_one,
           abort_signature_two=(p_decision).signature_two,
           abort_signing_key_one=(p_decision).signing_key_one,
           abort_signing_key_two=(p_decision).signing_key_two
     WHERE handoff_id=(p_prepared).operation_id;
END
$function$;
CREATE FUNCTION claimcore_witness.abort_writer_handoff(p_identity claimcore_witness.installation_identity, p_prepared claimcore_witness.journal_ticket, p_decision claimcore_witness.handoff_abort_decision, p_delivery claimcore_witness.writer_delivery)
RETURNS TABLE(sequence bigint,entry_hash bytea,payload_sha256 bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_handoff claimcore_witness.writer_handoffs%ROWTYPE;
    v_sequence bigint;
    v_hash bytea;
    v_payload_hash bytea;
    v_candidate_hash bytea;
BEGIN
    PERFORM claimcore_witness.validate_handoff_abort(p_identity,p_prepared,p_decision,p_delivery);
    v_candidate_hash := pg_catalog.sha256(
        pg_catalog.convert_to('CLAIMCORE_WRITER_HANDOFF_ABORT_V1:', 'UTF8') ||
        (p_decision).canonical || (p_decision).signature_one || (p_decision).signature_two);
    v_payload_hash := pg_catalog.sha256((p_delivery).encrypted_payload);
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    SELECT * INTO STRICT v_handoff
      FROM claimcore_witness.writer_handoffs h WHERE h.handoff_id=(p_prepared).operation_id FOR UPDATE;
    PERFORM claimcore_witness.verify_handoff_abort_source(p_identity,p_prepared,p_delivery);
    IF v_handoff.abort_sequence IS NOT NULL THEN
    PERFORM claimcore_witness.verify_handoff_abort_retry(p_identity,p_prepared,p_decision,p_delivery);
        sequence := v_handoff.abort_sequence;
        entry_hash := v_handoff.abort_hash;
        payload_sha256 := v_payload_hash;
        RETURN NEXT;
        RETURN;
    END IF;
    PERFORM claimcore_witness.require_handoff_abort(p_prepared);
    SELECT inserted.sequence,inserted.entry_hash,inserted.payload_sha256
      INTO v_sequence,v_hash,v_payload_hash FROM claimcore_witness.insert_journal_entry(p_identity,ROW((p_prepared).sequence,(p_prepared).entry_hash)::claimcore_witness.journal_tip,ROW('INSTALLATION',NULL)::claimcore_witness.journal_subject,ROW((p_prepared).operation_id,'ABORTED_BEFORE_COMMIT',(p_delivery).key_id,(p_delivery).encrypted_payload)::claimcore_witness.journal_request) inserted;
    PERFORM claimcore_witness.record_handoff_abort(p_prepared,p_decision,v_candidate_hash,v_sequence,v_hash);
    UPDATE claimcore_witness.installation
       SET tip_sequence=v_sequence,tip_hash=v_hash WHERE singleton;
    sequence := v_sequence;
    entry_hash := v_hash;
    payload_sha256 := v_payload_hash;
    RETURN NEXT;
END

$function$;
