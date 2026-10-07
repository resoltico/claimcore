CREATE FUNCTION claimcore_witness.validate_installation_identity(p_value claimcore_witness.installation_identity)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).installation_id IS NULL OR (p_value).lineage_id IS NULL OR (p_value).epoch IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).installation_id='00000000-0000-0000-0000-000000000000'::uuid OR (p_value).lineage_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_journal_tip(p_value claimcore_witness.journal_tip)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).sequence IS NULL OR (p_value).entry_hash IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_journal_ticket(p_value claimcore_witness.journal_ticket)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).operation_id IS NULL OR (p_value).sequence IS NULL OR (p_value).entry_hash IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).operation_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_journal_request(p_value claimcore_witness.journal_request)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).operation_id IS NULL OR (p_value).phase IS NULL OR (p_value).key_id IS NULL OR (p_value).encrypted_payload IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).operation_id='00000000-0000-0000-0000-000000000000'::uuid OR (p_value).key_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_journal_payload(p_value claimcore_witness.journal_payload)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).key_id IS NULL OR (p_value).encrypted_payload IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).key_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_writer_delivery(p_value claimcore_witness.writer_delivery)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).key_id IS NULL OR (p_value).encrypted_payload IS NULL OR (p_value).writer_capability IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).key_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.validate_key_rotation(p_value claimcore_witness.key_rotation)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    IF (p_value).operation_id IS NULL OR (p_value).old_key_id IS NULL OR (p_value).new_key_id IS NULL OR (p_value).key_check IS NULL THEN
        RAISE EXCEPTION 'incomplete witness proof group';
    END IF;
    IF (p_value).operation_id='00000000-0000-0000-0000-000000000000'::uuid OR (p_value).old_key_id='00000000-0000-0000-0000-000000000000'::uuid OR (p_value).new_key_id='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'invalid witness proof identity';
    END IF;
END
$function$;
