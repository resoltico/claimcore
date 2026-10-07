-- Current installation baseline. Executed only by SchemaBaseline.initialize in one transaction.
-- No historical installation may be adopted, upgraded, reset or repaired by this script.
CREATE SCHEMA claimcore;
REVOKE ALL ON SCHEMA claimcore FROM PUBLIC;

CREATE TABLE claimcore.schema_baseline (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    baseline_id text NOT NULL CHECK (baseline_id ~ '^[a-z][a-z0-9-]{0,63}$'),
    script_sha256 text NOT NULL CHECK (script_sha256 ~ '^[0-9a-f]{64}$'),
    installed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    installed_by text NOT NULL DEFAULT session_user
);

CREATE TABLE claimcore.cases (
    case_id uuid NOT NULL UNIQUE CHECK (case_id <> '00000000-0000-0000-0000-000000000000'),
    incident_date date NOT NULL,
    incident_notification_date date NOT NULL,
    incident_country text NOT NULL,
    claimant_name text NOT NULL,
    insurer_name text NOT NULL,
    claimed_amount numeric NOT NULL,
    claimed_currency text NOT NULL,
    case_reference text COLLATE "C" PRIMARY KEY,
    payment_decision_date date,
    payable_amount numeric,
    payable_currency text,
    payment_date date,
    status text NOT NULL CHECK (status IN ('OPENED', 'CLOSED')),
    revision bigint NOT NULL CHECK (revision > 0 AND revision < 9223372036854775807),
    disposition text NOT NULL DEFAULT 'ACTIVE'
        CHECK (disposition IN ('ACTIVE', 'VOIDED_DATA_ENTRY_ERROR')),
    privacy_phase text NOT NULL DEFAULT 'ACTIVE'
        CHECK (privacy_phase IN (
            'ACTIVE', 'ERASURE_REQUESTED', 'ERASURE_PENDING',
            'PAYLOAD_ERASED_SUPPRESSION_RETAINED', 'ERASURE_FINAL'
        )),
    lifecycle_sequence bigint NOT NULL DEFAULT 0 CHECK (lifecycle_sequence >= 0),
    lifecycle_event_hash bytea NOT NULL DEFAULT decode(repeat('00', 32), 'hex')
        CHECK (octet_length(lifecycle_event_hash) = 32),
    UNIQUE (case_id, case_reference),
    CONSTRAINT reference_shape CHECK (
        char_length(case_reference) BETWEEN 1 AND 80
        AND btrim(case_reference) = case_reference
        AND case_reference !~ '[[:cntrl:]]'
    ),
    CONSTRAINT name_shape CHECK (
        char_length(btrim(incident_country)) BETWEEN 1 AND 100
        AND char_length(btrim(claimant_name)) BETWEEN 1 AND 200
        AND char_length(btrim(insurer_name)) BETWEEN 1 AND 200
        AND incident_country = btrim(incident_country)
        AND claimant_name = btrim(claimant_name)
        AND insurer_name = btrim(insurer_name)
        AND incident_country !~ '[[:cntrl:]]'
        AND claimant_name !~ '[[:cntrl:]]'
        AND insurer_name !~ '[[:cntrl:]]'
    ),
    CONSTRAINT claimed_money CHECK (
        claimed_amount >= 0 AND claimed_amount <= 999999999999999999.9999
        AND scale(claimed_amount) <= 4 AND claimed_currency ~ '^[A-Z]{3}$'
    ),
    CONSTRAINT decision_group CHECK (
        (payment_decision_date IS NULL AND payable_amount IS NULL AND payable_currency IS NULL)
        OR (payment_decision_date IS NOT NULL AND payable_amount IS NOT NULL AND payable_currency IS NOT NULL)
    ),
    CONSTRAINT payable_money CHECK (
        payable_amount IS NULL OR (
            payable_amount >= 0 AND payable_amount <= 999999999999999999.9999
            AND scale(payable_amount) <= 4 AND payable_currency ~ '^[A-Z]{3}$'
        )
    ),
    CONSTRAINT date_bounds CHECK (
        incident_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'
        AND incident_notification_date BETWEEN incident_date AND DATE '9999-12-31'
        AND (payment_decision_date IS NULL OR payment_decision_date BETWEEN incident_notification_date AND DATE '9999-12-31')
        AND (payment_date IS NULL OR payment_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31')
    ),
    CONSTRAINT paid_requires_decision CHECK (
        payment_date IS NULL OR (
            payment_decision_date IS NOT NULL AND payable_amount > 0 AND payment_date >= payment_decision_date
        )
    )
);

CREATE TABLE claimcore.case_changes (
    operation_id uuid PRIMARY KEY CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL,
    case_reference text COLLATE "C" NOT NULL,
    preparer_actor_id uuid NOT NULL,
    importer_actor_id uuid,
    submitter_actor_id uuid,
    resolver_actor_id uuid,
    accepted_actor_id uuid NOT NULL,
    grant_revision bigint NOT NULL CHECK (grant_revision > 0),
    revision bigint NOT NULL CHECK (revision > 0 AND revision < 9223372036854775807),
    command_name text NOT NULL CHECK (command_name IN (
        'OPEN', 'AMEND_REGISTRATION', 'DECIDE', 'WITHDRAW_DECISION',
        'RECORD_PAYMENT', 'CLEAR_PAYMENT', 'CLOSE', 'REOPEN', 'CORRECT_CASE'
    )),
    rule_revision smallint NOT NULL CHECK (rule_revision = 2),
    request_format_version smallint NOT NULL CHECK (request_format_version = 1),
    request_sha256 text NOT NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    canonical_request bytea NOT NULL CHECK (octet_length(canonical_request) BETWEEN 1 AND 65536),
    effective_business_date date NOT NULL CHECK (
        effective_business_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'
    ),
    observed_utc_instant timestamptz NOT NULL,
    snapshot_version smallint NOT NULL CHECK (snapshot_version = 2),
    snapshot bytea NOT NULL CHECK (octet_length(snapshot) BETWEEN 1 AND 65536),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    UNIQUE (witness_epoch, witness_sequence),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (case_reference, revision),
    FOREIGN KEY (case_id, case_reference) REFERENCES claimcore.cases(case_id, case_reference),
    CONSTRAINT case_changes_actor_phase CHECK (
        (submitter_actor_id IS NOT NULL) <> (resolver_actor_id IS NOT NULL)
        AND accepted_actor_id = COALESCE(submitter_actor_id, resolver_actor_id)
    )
);

CREATE TABLE claimcore.installation_lineage (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    installation_id uuid NOT NULL UNIQUE
        CHECK (installation_id <> '00000000-0000-0000-0000-000000000000'),
    lineage_id uuid NOT NULL UNIQUE
        CHECK (lineage_id <> '00000000-0000-0000-0000-000000000000'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    witness_epoch bigint NOT NULL DEFAULT 1 CHECK (witness_epoch > 0),
    writer_generation bigint NOT NULL DEFAULT 1 CHECK (writer_generation > 0),
    loss_retired boolean NOT NULL DEFAULT false,
    loss_retirement_id uuid,
    loss_retirement_intent_sequence bigint CHECK (
        loss_retirement_intent_sequence IS NULL OR loss_retirement_intent_sequence > 0
    ),
    loss_retirement_intent_hash bytea CHECK (
        loss_retirement_intent_hash IS NULL OR octet_length(loss_retirement_intent_hash) = 32
    ),
    CONSTRAINT installation_loss_retirement_shape CHECK (
        (NOT loss_retired AND loss_retirement_id IS NULL
            AND loss_retirement_intent_sequence IS NULL AND loss_retirement_intent_hash IS NULL)
        OR (loss_retired AND loss_retirement_id IS NOT NULL
            AND loss_retirement_intent_sequence IS NOT NULL
            AND loss_retirement_intent_hash IS NOT NULL)
    ),
    data_use_scope text NOT NULL CHECK (data_use_scope IN ('SYNTHETIC_ONLY','REAL_DATA')),
    data_use_phase text NOT NULL CHECK (data_use_phase IN ('BOOTSTRAP_NO_CASES','ACTIVE')),
    data_use_activation_event_id uuid,
    data_use_activation_sequence bigint CHECK (
        data_use_activation_sequence IS NULL OR data_use_activation_sequence > 0
    ),
    data_use_activation_hash bytea CHECK (
        data_use_activation_hash IS NULL OR octet_length(data_use_activation_hash) = 32
    ),
    writer_handoff_event_id uuid,
    writer_handoff_sequence bigint CHECK (writer_handoff_sequence IS NULL OR writer_handoff_sequence > 0),
    writer_handoff_hash bytea CHECK (
        writer_handoff_hash IS NULL OR octet_length(writer_handoff_hash) = 32
    ),
    writer_activation_pending boolean NOT NULL DEFAULT false,
    writer_activation_event_id uuid,
    writer_activation_sequence bigint CHECK (
        writer_activation_sequence IS NULL OR writer_activation_sequence > 0
    ),
    writer_activation_hash bytea CHECK (
        writer_activation_hash IS NULL OR octet_length(writer_activation_hash) = 32
    ),
    last_aborted_handoff_id uuid,
    last_aborted_handoff_sequence bigint CHECK (
        last_aborted_handoff_sequence IS NULL OR last_aborted_handoff_sequence > 0
    ),
    last_aborted_handoff_hash bytea CHECK (
        last_aborted_handoff_hash IS NULL OR octet_length(last_aborted_handoff_hash) = 32
    ),
    business_time_zone text NOT NULL,
    suppression_key_id uuid NOT NULL UNIQUE
        CHECK (suppression_key_id <> '00000000-0000-0000-0000-000000000000'),
    suppression_key_check bytea NOT NULL
        CHECK (octet_length(suppression_key_check) = 32),
    CONSTRAINT installation_lineage_business_time_zone_shape CHECK (
        char_length(business_time_zone) BETWEEN 1 AND 128
        AND business_time_zone = btrim(business_time_zone)
        AND business_time_zone !~ '[[:cntrl:]]'
    ),
    CONSTRAINT installation_lineage_data_use_shape CHECK (
        (data_use_scope = 'SYNTHETIC_ONLY' AND data_use_phase = 'ACTIVE'
            AND data_use_activation_event_id IS NULL AND data_use_activation_sequence IS NULL
            AND data_use_activation_hash IS NULL)
        OR (data_use_scope = 'REAL_DATA' AND
            ((data_use_phase = 'BOOTSTRAP_NO_CASES'
                AND data_use_activation_event_id IS NULL AND data_use_activation_sequence IS NULL
                AND data_use_activation_hash IS NULL)
             OR (data_use_phase = 'ACTIVE' AND data_use_activation_event_id IS NOT NULL
                AND data_use_activation_sequence IS NOT NULL AND data_use_activation_hash IS NOT NULL)))
    ),
    CONSTRAINT installation_lineage_writer_handoff_shape CHECK (
        (writer_generation = 1 AND writer_handoff_event_id IS NULL
            AND writer_handoff_sequence IS NULL AND writer_handoff_hash IS NULL
            AND NOT writer_activation_pending AND writer_activation_event_id IS NULL
            AND writer_activation_sequence IS NULL AND writer_activation_hash IS NULL)
        OR (writer_generation > 1 AND writer_handoff_event_id IS NOT NULL
            AND writer_handoff_sequence IS NOT NULL AND writer_handoff_hash IS NOT NULL
            AND ((writer_activation_pending AND writer_activation_event_id IS NULL
                AND writer_activation_sequence IS NULL AND writer_activation_hash IS NULL)
                OR (NOT writer_activation_pending AND writer_activation_event_id IS NOT NULL
                AND writer_activation_sequence > writer_handoff_sequence
                AND writer_activation_hash IS NOT NULL)))
    ),
    CONSTRAINT installation_lineage_abort_ticket_shape CHECK (
        (last_aborted_handoff_id IS NULL AND last_aborted_handoff_sequence IS NULL
            AND last_aborted_handoff_hash IS NULL)
        OR (last_aborted_handoff_id IS NOT NULL AND last_aborted_handoff_sequence IS NOT NULL
            AND last_aborted_handoff_hash IS NOT NULL)
    )
);
