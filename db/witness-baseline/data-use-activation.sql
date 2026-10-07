CREATE FUNCTION claimcore_witness.validate_data_use_activation(p_identity claimcore_witness.installation_identity, p_plan claimcore_witness.data_use_activation_plan, p_previous claimcore_witness.journal_tip, p_payloads claimcore_witness.activation_payloads)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    PERFORM claimcore_witness.validate_installation_identity(p_identity);
    PERFORM claimcore_witness.validate_data_use_activation_plan(p_plan);
    PERFORM claimcore_witness.validate_journal_tip(p_previous);
    PERFORM claimcore_witness.validate_activation_payloads(p_payloads);
    IF session_user <> 'claimcore_witness_owner'
       OR (p_plan).activation_id IS NULL OR (p_previous).sequence IS NULL OR (p_previous).sequence < 0
       OR (p_previous).entry_hash IS NULL OR octet_length((p_previous).entry_hash) <> 32
       OR (p_plan).canonical IS NULL OR octet_length((p_plan).canonical) NOT BETWEEN 1 AND 8192
       OR (p_payloads).key_id IS NULL OR (p_payloads).candidate IS NULL
       OR octet_length((p_payloads).candidate) NOT BETWEEN 1 AND 1048576
       OR (p_payloads).settlement IS NULL
       OR octet_length((p_payloads).settlement) NOT BETWEEN 1 AND 1048576 THEN
        RAISE EXCEPTION 'invalid data-use activation';
    END IF;

END
$function$;
CREATE FUNCTION claimcore_witness.verify_data_use_activation_identity(p_identity claimcore_witness.installation_identity, p_payloads claimcore_witness.activation_payloads)
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
       OR v_installation.data_use_scope <> 'REAL_DATA'
       OR v_installation.active_key_id <> (p_payloads).key_id THEN
        RAISE EXCEPTION 'data-use installation identity diverged';
    END IF;

END
$function$;
CREATE FUNCTION claimcore_witness.verify_data_use_activation_retry(p_identity claimcore_witness.installation_identity, p_plan claimcore_witness.data_use_activation_plan)
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
        IF v_installation.data_use_activation_event_id <> (p_plan).activation_id
           OR v_installation.data_use_activation_canonical <> (p_plan).canonical
           OR v_installation.data_use_activation_sequence
              <> v_installation.data_use_activation_intent_sequence + 1
           OR NOT EXISTS (
               SELECT 1 FROM claimcore_witness.journal i
               JOIN claimcore_witness.journal s ON s.installation_id=i.installation_id
                 AND s.operation_id=i.operation_id AND s.phase='SETTLED_AUTHORITY'
               WHERE i.installation_id=(p_identity).installation_id
                 AND i.operation_id=(p_plan).activation_id AND i.phase='INTENT'
                 AND i.sequence=v_installation.data_use_activation_intent_sequence
                 AND i.entry_hash=v_installation.data_use_activation_intent_hash
                 AND s.sequence=v_installation.data_use_activation_sequence
                 AND s.entry_hash=v_installation.data_use_activation_hash
           ) THEN
            RAISE EXCEPTION 'divergent data-use activation retry';
        END IF;
END
$function$;
CREATE FUNCTION claimcore_witness.require_data_use_activation(p_identity claimcore_witness.installation_identity, p_previous claimcore_witness.journal_tip)
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
    IF v_installation.data_use_phase <> 'BOOTSTRAP_NO_CASES'
       OR v_installation.handoff_pending OR v_installation.activation_pending
       OR v_installation.tip_sequence <> (p_previous).sequence
       OR v_installation.tip_hash <> (p_previous).entry_hash
       OR EXISTS (
           SELECT 1 FROM claimcore_witness.journal i
           WHERE i.installation_id=(p_identity).installation_id AND i.phase='INTENT'
             AND NOT EXISTS (
                 SELECT 1 FROM claimcore_witness.journal s
                 WHERE s.installation_id=i.installation_id
                   AND s.operation_id=i.operation_id
                   AND s.phase IN ('SETTLED_ACCEPTED','SETTLED_REVOKED',
                                   'SETTLED_AUTHORITY','ABORTED_BEFORE_COMMIT')
             )
       ) THEN
        RAISE EXCEPTION 'data-use activation is not quiescent';
    END IF;

END
$function$;
CREATE FUNCTION claimcore_witness.record_data_use_activation(p_plan claimcore_witness.data_use_activation_plan, v_intent_sequence bigint, v_intent_hash bytea, v_settlement_sequence bigint, v_settlement_hash bytea)
RETURNS void
LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$
BEGIN
    IF current_user <> 'claimcore_witness_owner' THEN
        RAISE EXCEPTION 'private witness function requires schema owner';
    END IF;
    UPDATE claimcore_witness.installation SET
        data_use_phase='ACTIVE',
        data_use_activation_event_id=(p_plan).activation_id,
        data_use_activation_intent_sequence=v_intent_sequence,
        data_use_activation_intent_hash=v_intent_hash,
        data_use_activation_sequence=v_settlement_sequence,
        data_use_activation_hash=v_settlement_hash,
        data_use_activation_canonical=(p_plan).canonical,
        tip_sequence=v_settlement_sequence,tip_hash=v_settlement_hash
      WHERE singleton;
END
$function$;
CREATE FUNCTION claimcore_witness.activate_data_use(p_identity claimcore_witness.installation_identity, p_plan claimcore_witness.data_use_activation_plan, p_previous claimcore_witness.journal_tip, p_payloads claimcore_witness.activation_payloads)
RETURNS TABLE(intent_sequence bigint,intent_hash bytea,settlement_sequence bigint,settlement_hash bytea)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, claimcore_witness, pg_temp
AS $function$

DECLARE
    v_installation claimcore_witness.installation%ROWTYPE;
    v_intent_sequence bigint;
    v_intent_hash bytea;
    v_settlement_sequence bigint;
    v_settlement_hash bytea;
    v_digest bytea;
BEGIN
    PERFORM claimcore_witness.validate_data_use_activation(p_identity,p_plan,p_previous,p_payloads);
    SELECT * INTO STRICT v_installation
      FROM claimcore_witness.installation WHERE singleton FOR UPDATE;
    PERFORM claimcore_witness.verify_data_use_activation_identity(p_identity,p_payloads);
    IF v_installation.data_use_phase = 'ACTIVE' THEN
    PERFORM claimcore_witness.verify_data_use_activation_retry(p_identity,p_plan);
        intent_sequence := v_installation.data_use_activation_intent_sequence;
        intent_hash := v_installation.data_use_activation_intent_hash;
        settlement_sequence := v_installation.data_use_activation_sequence;
        settlement_hash := v_installation.data_use_activation_hash;
        RETURN NEXT;
        RETURN;
    END IF;

    PERFORM claimcore_witness.require_data_use_activation(p_identity,p_previous);
    SELECT inserted.sequence,inserted.entry_hash,inserted.payload_sha256
      INTO v_intent_sequence,v_intent_hash,v_digest FROM claimcore_witness.insert_journal_entry(p_identity,ROW((p_previous).sequence,(p_previous).entry_hash)::claimcore_witness.journal_tip,ROW('INSTALLATION',NULL)::claimcore_witness.journal_subject,ROW((p_plan).activation_id,'INTENT',(p_payloads).key_id,(p_payloads).candidate)::claimcore_witness.journal_request) inserted;
    SELECT inserted.sequence,inserted.entry_hash,inserted.payload_sha256
      INTO v_settlement_sequence,v_settlement_hash,v_digest FROM claimcore_witness.insert_journal_entry(p_identity,ROW(v_intent_sequence,v_intent_hash)::claimcore_witness.journal_tip,ROW('INSTALLATION',NULL)::claimcore_witness.journal_subject,ROW((p_plan).activation_id,'SETTLED_AUTHORITY',(p_payloads).key_id,(p_payloads).settlement)::claimcore_witness.journal_request) inserted;
    PERFORM claimcore_witness.record_data_use_activation(p_plan,v_intent_sequence,v_intent_hash,v_settlement_sequence,v_settlement_hash);
    intent_sequence := v_intent_sequence;
    intent_hash := v_intent_hash;
    settlement_sequence := v_settlement_sequence;
    settlement_hash := v_settlement_hash;
    RETURN NEXT;
END

$function$;
