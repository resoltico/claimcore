CREATE FUNCTION claimcore_witness.delete_prune_targets(p_identity claimcore_witness.installation_identity, p_source claimcore_witness.prune_source)
RETURNS bigint
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
DECLARE
    v_deleted bigint := 0;
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
        DELETE FROM claimcore_witness.journal_payloads p
          USING claimcore_witness.journal j
          WHERE p.installation_id=(p_identity).installation_id
            AND p.installation_id=j.installation_id AND p.sequence=j.sequence
            AND j.scope_kind='CASE' AND j.subject_case_id=(p_source).case_id
            AND j.sequence<=((p_source).cutoff).sequence;
        GET DIAGNOSTICS v_deleted = ROW_COUNT;
        IF v_deleted <> (p_source).target_count THEN
            RAISE EXCEPTION 'witness prune deleted a divergent set';
        END IF;
    RETURN v_deleted;
END
$function$;
CREATE FUNCTION claimcore_witness.settle_and_prune(p_identity claimcore_witness.installation_identity, p_source claimcore_witness.prune_source, p_authority claimcore_witness.prune_authority, p_delivery claimcore_witness.writer_delivery)
RETURNS TABLE(sequence bigint,entry_hash bytea,payload_sha256 bytea,deleted_count bigint)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_existing claimcore_witness.journal%ROWTYPE;
    v_deleted bigint := 0;
BEGIN
    PERFORM claimcore_witness.validate_prune_proof(p_identity,p_source,p_authority,p_delivery);
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    PERFORM claimcore_witness.require_prune_writer(p_identity,p_authority,p_delivery);
    PERFORM claimcore_witness.verify_prune_source(p_identity,p_source,p_authority,p_delivery);
    v_existing:=claimcore_witness.find_prune_settlement(p_identity,p_source,p_authority,p_delivery);
    PERFORM claimcore_witness.verify_prune_approvals(p_identity,p_source,p_authority);
    PERFORM claimcore_witness.verify_prune_phase_set(p_identity,p_source,p_authority,v_existing);
    PERFORM claimcore_witness.verify_prune_targets(p_identity,p_source,v_existing);
    IF v_existing.operation_id IS NOT NULL THEN
        sequence := v_existing.sequence;
        entry_hash := v_existing.entry_hash;
        payload_sha256 := v_existing.payload_sha256;
        deleted_count := 0;
        RETURN NEXT;
        RETURN;
    END IF;

    SELECT appended.sequence, appended.entry_hash, appended.payload_sha256
      INTO sequence, entry_hash, payload_sha256
      FROM claimcore_witness.append(ROW((p_identity).installation_id,(p_identity).lineage_id,(p_identity).epoch)::claimcore_witness.installation_identity,ROW(NULL,NULL)::claimcore_witness.journal_subject,ROW((p_authority).event_id,'SETTLED_AUTHORITY',(p_delivery).key_id,(p_delivery).encrypted_payload)::claimcore_witness.journal_request,(p_delivery).writer_capability) AS appended;
    v_deleted:=claimcore_witness.delete_prune_targets(p_identity,p_source);
    deleted_count := v_deleted;
    RETURN NEXT;
END

$function$;
