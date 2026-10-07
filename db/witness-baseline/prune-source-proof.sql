CREATE FUNCTION claimcore_witness.validate_prune_proof(p_identity claimcore_witness.installation_identity, p_source claimcore_witness.prune_source, p_authority claimcore_witness.prune_authority, p_delivery claimcore_witness.writer_delivery)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    PERFORM claimcore_witness.validate_installation_identity(p_identity);
    PERFORM claimcore_witness.validate_prune_source(p_source);
    PERFORM claimcore_witness.validate_prune_authority(p_authority);
    PERFORM claimcore_witness.validate_writer_delivery(p_delivery);
    IF session_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'witness ciphertext prune requires schema owner';
    END IF;
    IF (p_authority).event_id IS NULL
       OR (p_source).case_id IS NULL OR ((p_source).purge).operation_id IS NULL
       OR (p_identity).installation_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR (p_identity).lineage_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR (p_authority).event_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR (p_source).case_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR ((p_source).purge).operation_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR (p_delivery).key_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR (p_delivery).key_id IS NULL OR ((p_authority).intent).sequence IS NULL OR ((p_source).cutoff).sequence IS NULL
       OR ((p_source).purge).sequence IS NULL OR (p_source).target_count IS NULL
       OR ((p_authority).approval_one).operation_id IS NULL OR ((p_authority).approval_two).operation_id IS NULL
       OR ((p_authority).approval_one).operation_id = ((p_authority).approval_two).operation_id
       OR ((p_authority).approval_one).operation_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR ((p_authority).approval_two).operation_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR ((p_authority).approval_one).sequence IS NULL OR ((p_authority).approval_two).sequence IS NULL
       OR ((p_authority).approval_one).entry_hash IS NULL OR ((p_authority).approval_two).entry_hash IS NULL
       OR ((p_authority).intent).entry_hash IS NULL OR ((p_source).purge).entry_hash IS NULL
       OR ((p_source).cutoff).entry_hash IS NULL OR (p_source).target_digest IS NULL
       OR (p_delivery).encrypted_payload IS NULL
       OR ((p_source).cutoff).sequence < 1 OR (p_source).target_count < 1
       OR ((p_authority).intent).sequence <= ((p_source).cutoff).sequence
       OR ((p_source).purge).sequence > ((p_source).cutoff).sequence
       OR octet_length(((p_authority).intent).entry_hash) <> 32
       OR octet_length(((p_source).purge).entry_hash) <> 32
       OR octet_length(((p_source).cutoff).entry_hash) <> 32 OR octet_length((p_source).target_digest) <> 32
       OR octet_length(((p_authority).approval_one).entry_hash) <> 32
       OR octet_length(((p_authority).approval_two).entry_hash) <> 32
       OR octet_length((p_delivery).encrypted_payload) NOT BETWEEN 1 AND 1048576
    THEN
        RAISE EXCEPTION 'invalid witness ciphertext prune proof';
    END IF;

END
$function$;
CREATE FUNCTION claimcore_witness.require_prune_writer(p_identity claimcore_witness.installation_identity, p_authority claimcore_witness.prune_authority, p_delivery claimcore_witness.writer_delivery)
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
       OR v_installation.tip_sequence < ((p_authority).intent).sequence THEN
        RAISE EXCEPTION 'witness prune identity or key diverged';
    END IF;
    IF v_installation.handoff_pending OR v_installation.activation_pending
       OR (p_delivery).writer_capability IS NULL OR octet_length((p_delivery).writer_capability) <> 32
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256((p_delivery).writer_capability) THEN
        RAISE EXCEPTION 'witness writer generation is fenced';
    END IF;

END
$function$;
CREATE FUNCTION claimcore_witness.verify_prune_source(p_identity claimcore_witness.installation_identity, p_source claimcore_witness.prune_source, p_authority claimcore_witness.prune_authority, p_delivery claimcore_witness.writer_delivery)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_intent claimcore_witness.journal%ROWTYPE;
    v_purge claimcore_witness.journal%ROWTYPE;
    v_purge_settled claimcore_witness.journal%ROWTYPE;
    v_cutoff claimcore_witness.journal%ROWTYPE;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    SELECT j.* INTO STRICT v_intent FROM claimcore_witness.journal j
      WHERE j.installation_id=(p_identity).installation_id AND j.sequence=((p_authority).intent).sequence;
    SELECT j.* INTO STRICT v_purge FROM claimcore_witness.journal j
      WHERE j.installation_id=(p_identity).installation_id AND j.sequence=((p_source).purge).sequence;
    SELECT j.* INTO STRICT v_purge_settled FROM claimcore_witness.journal j
      WHERE j.installation_id=(p_identity).installation_id
        AND j.operation_id=((p_source).purge).operation_id AND j.phase='SETTLED_AUTHORITY';
    SELECT j.* INTO STRICT v_cutoff FROM claimcore_witness.journal j
      WHERE j.installation_id=(p_identity).installation_id AND j.sequence=((p_source).cutoff).sequence;
    IF v_intent.lineage_id <> (p_identity).lineage_id OR v_intent.epoch <> (p_identity).epoch
       OR v_intent.operation_id <> (p_authority).event_id OR v_intent.phase <> 'INTENT'
       OR v_intent.scope_kind <> 'CASE' OR v_intent.subject_case_id <> (p_source).case_id
       OR v_intent.entry_hash <> ((p_authority).intent).entry_hash OR v_intent.key_id <> (p_delivery).key_id
       OR v_purge.operation_id <> ((p_source).purge).operation_id
       OR v_purge.phase <> 'INTENT'
       OR v_purge.subject_case_id <> (p_source).case_id
       OR v_purge.entry_hash <> ((p_source).purge).entry_hash
       OR v_purge.scope_kind <> 'CASE' OR v_purge.epoch <> (p_identity).epoch
       OR v_purge_settled.subject_case_id <> (p_source).case_id
       OR v_purge_settled.scope_kind <> 'CASE'
       OR v_purge_settled.epoch <> (p_identity).epoch
       OR v_purge_settled.sequence <= v_purge.sequence
       OR v_purge_settled.sequence > ((p_source).cutoff).sequence
       OR v_cutoff.entry_hash <> ((p_source).cutoff).entry_hash
       OR v_cutoff.epoch <> (p_identity).epoch THEN
        RAISE EXCEPTION 'witness prune source proof diverged';
    END IF;

END
$function$;
CREATE FUNCTION claimcore_witness.find_prune_settlement(p_identity claimcore_witness.installation_identity, p_source claimcore_witness.prune_source, p_authority claimcore_witness.prune_authority, p_delivery claimcore_witness.writer_delivery)
RETURNS claimcore_witness.journal
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_existing claimcore_witness.journal%ROWTYPE;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    SELECT * INTO STRICT v_installation FROM claimcore_witness.installation WHERE singleton;
    SELECT j.* INTO v_existing FROM claimcore_witness.journal j
      WHERE j.installation_id=(p_identity).installation_id
        AND j.operation_id=(p_authority).event_id AND j.phase='SETTLED_AUTHORITY';
    IF v_existing.operation_id IS NULL THEN
        IF v_installation.active_key_id <> (p_delivery).key_id THEN
            RAISE EXCEPTION 'new witness prune settlement requires active key';
        END IF;
    ELSIF v_existing.lineage_id <> (p_identity).lineage_id
       OR v_existing.epoch <> (p_identity).epoch
       OR v_existing.sequence <= ((p_authority).intent).sequence
       OR v_existing.scope_kind <> 'CASE'
       OR v_existing.subject_case_id <> (p_source).case_id
       OR v_existing.key_id <> (p_delivery).key_id
       OR v_existing.payload_sha256 <> pg_catalog.sha256((p_delivery).encrypted_payload)
       OR NOT EXISTS (
           SELECT 1 FROM claimcore_witness.journal_payloads p
           WHERE p.installation_id=v_existing.installation_id
             AND p.sequence=v_existing.sequence
             AND p.subject_case_id=(p_source).case_id
             AND p.encrypted_payload=(p_delivery).encrypted_payload
       ) THEN
        RAISE EXCEPTION 'existing witness prune settlement diverged';
    END IF;
    RETURN v_existing;
END
$function$;
