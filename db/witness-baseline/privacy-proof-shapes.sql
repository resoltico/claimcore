CREATE FUNCTION claimcore_witness.validate_prune_source(p_value claimcore_witness.prune_source)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).case_id IS NULL OR (p_value).purge IS NULL OR (p_value).cutoff IS NULL OR (p_value).target_count IS NULL OR (p_value).target_digest IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).case_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
    PERFORM claimcore_witness.validate_journal_ticket((p_value).purge);
    PERFORM claimcore_witness.validate_journal_tip((p_value).cutoff);
END
$function$;
CREATE FUNCTION claimcore_witness.validate_prune_authority(p_value claimcore_witness.prune_authority)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).event_id IS NULL OR (p_value).intent IS NULL OR (p_value).approval_one IS NULL OR (p_value).approval_two IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).event_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
    PERFORM claimcore_witness.validate_journal_tip((p_value).intent);
    PERFORM claimcore_witness.validate_journal_ticket((p_value).approval_one);
    PERFORM claimcore_witness.validate_journal_ticket((p_value).approval_two);
END
$function$;
