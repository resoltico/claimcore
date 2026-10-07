CREATE FUNCTION claimcore_witness.guard_loss_journal()
RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
BEGIN
    SELECT * INTO STRICT v_installation FROM claimcore_witness.installation WHERE singleton;
    IF v_installation.loss_retired
       OR (v_installation.loss_retirement_pending AND NOT (
           NEW.operation_id=v_installation.loss_retirement_id
           AND NEW.phase='SETTLED_AUTHORITY'
           AND NEW.scope_kind='INSTALLATION'
           AND NEW.subject_case_id IS NULL
           AND NEW.sequence=v_installation.loss_retirement_intent_sequence + 1
       )) THEN
        RAISE EXCEPTION 'installation loss retirement fences witness journal';
    END IF;
    RETURN NEW;
END

$function$;
CREATE FUNCTION claimcore_witness.guard_loss_installation()
RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

BEGIN
    IF OLD.loss_retired OR (OLD.loss_retirement_pending AND NOT (
        NEW.loss_retired AND NOT NEW.loss_retirement_pending
        AND NEW.loss_retirement_id=OLD.loss_retirement_id
        AND NEW.loss_retirement_intent_sequence=OLD.loss_retirement_intent_sequence
        AND NEW.loss_retirement_intent_hash=OLD.loss_retirement_intent_hash
        AND NEW.loss_retirement_sequence=OLD.loss_retirement_intent_sequence + 1
        AND NEW.tip_sequence=OLD.tip_sequence + 1
    )) THEN
        RAISE EXCEPTION 'installation loss retirement is terminal';
    END IF;
    RETURN NEW;
END

$function$;
CREATE FUNCTION claimcore_witness.acquire_read_fence(p_identity claimcore_witness.installation_identity, p_writer_capability bytea)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
BEGIN
    PERFORM claimcore_witness.validate_installation_identity(p_identity);
    IF session_user <> 'claimcore_witness_writer'
       OR p_writer_capability IS NULL OR octet_length(p_writer_capability) <> 32 THEN
        RAISE EXCEPTION 'writer read fence is unavailable';
    END IF;
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR SHARE;
    IF v_installation.installation_id <> (p_identity).installation_id
       OR v_installation.lineage_id <> (p_identity).lineage_id
       OR v_installation.epoch <> (p_identity).epoch
       OR v_installation.handoff_pending
       OR v_installation.activation_pending
       OR v_installation.loss_retirement_pending
       OR v_installation.loss_retired
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256(p_writer_capability) THEN
        RAISE EXCEPTION 'writer read fence is unavailable';
    END IF;
    RETURN v_installation.writer_generation;
END

$function$;
CREATE FUNCTION claimcore_witness.rotate_key(p_identity claimcore_witness.installation_identity, p_rotation claimcore_witness.key_rotation, p_rotation_envelope bytea, p_writer_capability bytea)
RETURNS TABLE(sequence bigint,entry_hash bytea,payload_sha256 bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
BEGIN
    PERFORM claimcore_witness.validate_installation_identity(p_identity);
    PERFORM claimcore_witness.validate_key_rotation(p_rotation);
    IF (p_rotation).old_key_id IS NULL OR (p_rotation).new_key_id IS NULL OR (p_rotation).old_key_id = (p_rotation).new_key_id
       OR (p_rotation).operation_id IS NULL OR (p_rotation).key_check IS NULL
       OR octet_length((p_rotation).key_check) NOT BETWEEN 32 AND 4096
    THEN
        RAISE EXCEPTION 'invalid witness key rotation';
    END IF;
    IF session_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'witness key rotation requires schema owner';
    END IF;
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    IF v_installation.installation_id <> (p_identity).installation_id
       OR v_installation.lineage_id <> (p_identity).lineage_id
       OR v_installation.epoch <> (p_identity).epoch
       OR v_installation.active_key_id <> (p_rotation).old_key_id THEN
        RAISE EXCEPTION 'witness key rotation identity mismatch';
    END IF;
    IF v_installation.handoff_pending OR v_installation.activation_pending
       OR p_writer_capability IS NULL OR octet_length(p_writer_capability) <> 32
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256(p_writer_capability) THEN
        RAISE EXCEPTION 'witness writer generation is fenced';
    END IF;
    UPDATE claimcore_witness.installation
       SET active_key_id = (p_rotation).new_key_id,
           key_check_envelope = (p_rotation).key_check WHERE singleton;
    RETURN QUERY SELECT appended.sequence, appended.entry_hash, appended.payload_sha256
      FROM claimcore_witness.append(ROW((p_identity).installation_id,(p_identity).lineage_id,(p_identity).epoch)::claimcore_witness.installation_identity,ROW('INSTALLATION',NULL)::claimcore_witness.journal_subject,ROW((p_rotation).operation_id,'KEY_ROTATED',(p_rotation).new_key_id,p_rotation_envelope)::claimcore_witness.journal_request,p_writer_capability) AS appended;
END

$function$;
