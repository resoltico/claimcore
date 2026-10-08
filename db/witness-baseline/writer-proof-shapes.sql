CREATE FUNCTION claimcore_witness.validate_handoff_plan(p_value claimcore_witness.handoff_plan)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).handoff_id IS NULL OR (p_value).old_generation IS NULL OR (p_value).new_capability_sha256 IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).handoff_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_handoff_approval(p_value claimcore_witness.handoff_approval)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).canonical IS NULL OR (p_value).signature IS NULL OR (p_value).signing_key_id IS NULL OR (p_value).approval_one_id IS NULL OR (p_value).approval_two_id IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).signing_key_id='00000000-0000-0000-0000-000000000000'::uuid OR (p_value).approval_one_id='00000000-0000-0000-0000-000000000000'::uuid OR (p_value).approval_two_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_writer_capabilities(p_value claimcore_witness.writer_capabilities)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).old_capability IS NULL OR (p_value).new_capability IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_signed_candidate(p_value claimcore_witness.signed_candidate)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).canonical IS NULL OR (p_value).signature IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_writer_activation_plan(p_value claimcore_witness.writer_activation_plan)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).handoff_id IS NULL OR (p_value).activation_id IS NULL OR (p_value).canonical IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).handoff_id='00000000-0000-0000-0000-000000000000'::uuid OR (p_value).activation_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_data_use_activation_plan(p_value claimcore_witness.data_use_activation_plan)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).activation_id IS NULL OR (p_value).canonical IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).activation_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_activation_payloads(p_value claimcore_witness.activation_payloads)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).key_id IS NULL OR (p_value).candidate IS NULL OR (p_value).settlement IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).key_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_handoff_abort_decision(p_value claimcore_witness.handoff_abort_decision)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).canonical IS NULL OR (p_value).signature_one IS NULL OR (p_value).signature_two IS NULL OR (p_value).signing_key_one IS NULL OR (p_value).signing_key_two IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).signing_key_one='00000000-0000-0000-0000-000000000000'::uuid OR (p_value).signing_key_two='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
