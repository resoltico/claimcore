CREATE FUNCTION claimcore_witness.validate_handoff_abort_release(p_identity claimcore_witness.installation_identity, p_aborted claimcore_witness.journal_ticket, p_old_writer_capability bytea)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    PERFORM claimcore_witness.validate_installation_identity(p_identity);
    PERFORM claimcore_witness.validate_journal_ticket(p_aborted);
    IF session_user <> 'claimcore_witness_owner'
       OR (p_aborted).operation_id IS NULL OR (p_aborted).sequence IS NULL OR (p_aborted).sequence < 1
       OR (p_aborted).entry_hash IS NULL OR octet_length((p_aborted).entry_hash) <> 32
       OR p_old_writer_capability IS NULL OR octet_length(p_old_writer_capability) <> 32
    THEN
        RAISE EXCEPTION 'invalid writer handoff abort release';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.release_aborted_writer_handoff(p_identity claimcore_witness.installation_identity, p_aborted claimcore_witness.journal_ticket, p_old_writer_capability bytea)
RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_handoff claimcore_witness.writer_handoffs%ROWTYPE;
BEGIN
    PERFORM claimcore_witness.validate_handoff_abort_release(p_identity,p_aborted,p_old_writer_capability);
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    SELECT * INTO STRICT v_handoff
      FROM claimcore_witness.writer_handoffs h WHERE h.handoff_id=(p_aborted).operation_id FOR UPDATE;
    IF v_installation.installation_id <> (p_identity).installation_id
       OR v_installation.lineage_id <> (p_identity).lineage_id OR v_installation.epoch <> (p_identity).epoch
       OR v_installation.writer_generation <> v_handoff.old_generation
       OR v_installation.writer_capability_sha256 <> pg_catalog.sha256(p_old_writer_capability)
       OR v_handoff.settlement_sequence IS NOT NULL
       OR v_handoff.abort_sequence <> (p_aborted).sequence
       OR v_handoff.abort_hash <> (p_aborted).entry_hash
       OR v_installation.tip_sequence <> (p_aborted).sequence
       OR v_installation.tip_hash <> (p_aborted).entry_hash THEN
        RAISE EXCEPTION 'writer handoff abort release diverged';
    END IF;
    IF NOT v_installation.handoff_pending THEN
        IF v_installation.last_aborted_handoff_id <> (p_aborted).operation_id
           OR v_installation.last_aborted_handoff_sequence <> (p_aborted).sequence
           OR v_installation.last_aborted_handoff_hash <> (p_aborted).entry_hash THEN
            RAISE EXCEPTION 'divergent writer handoff abort release retry';
        END IF;
        RETURN true;
    END IF;
    UPDATE claimcore_witness.installation
       SET handoff_pending=false,last_aborted_handoff_id=(p_aborted).operation_id,
           last_aborted_handoff_sequence=(p_aborted).sequence,
           last_aborted_handoff_hash=(p_aborted).entry_hash
     WHERE singleton;
    RETURN true;
END

$function$;
