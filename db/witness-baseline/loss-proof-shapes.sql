CREATE FUNCTION claimcore_witness.validate_owner_signature(p_value claimcore_witness.owner_signature)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).signature IS NULL OR (p_value).signer_id IS NULL OR (p_value).actor_id IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).signer_id='00000000-0000-0000-0000-000000000000'::uuid OR (p_value).actor_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_owner_signature_pair(p_value claimcore_witness.owner_signature_pair)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).first IS NULL OR (p_value).second IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    PERFORM claimcore_witness.validate_owner_signature((p_value).first);
    PERFORM claimcore_witness.validate_owner_signature((p_value).second);
END
$function$;
CREATE FUNCTION claimcore_witness.validate_loss_retirement_plan(p_value claimcore_witness.loss_retirement_plan)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).retirement_id IS NULL OR (p_value).canonical IS NULL OR (p_value).operation_set_kind IS NULL OR (p_value).known_operation_count IS NULL OR (p_value).known_operation_digest IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).retirement_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_loss_settlement_proof(p_value claimcore_witness.loss_settlement_proof)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).intent IS NULL OR (p_value).primary_candidate_sha256 IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    PERFORM claimcore_witness.validate_journal_ticket((p_value).intent);
END
$function$;
