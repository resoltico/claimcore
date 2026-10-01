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

-- Individual authority is distinct from OIDC claims and from case business fields.
-- The singleton revision is locked before any protected mutation, including grant changes.
CREATE TABLE claimcore.authority_tip (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    revision bigint NOT NULL CHECK (revision >= 0)
);
INSERT INTO claimcore.authority_tip (singleton, revision) VALUES (true, 0);

-- One witnessed transition after the first independent backup-health certificate. The
-- installation's data-use scope never changes, including after a restore or later outage.
CREATE TABLE claimcore.installation_data_use_activations (
    activation_id uuid PRIMARY KEY CHECK (activation_id <> '00000000-0000-0000-0000-000000000000'),
    installation_id uuid NOT NULL,
    lineage_id uuid NOT NULL,
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    writer_generation bigint NOT NULL CHECK (writer_generation > 0),
    activation_plan_sha256 bytea NOT NULL CHECK (octet_length(activation_plan_sha256) = 32),
    health_certificate_sha256 bytea NOT NULL CHECK (octet_length(health_certificate_sha256) = 32),
    approval_one_id uuid NOT NULL,
    approval_two_id uuid NOT NULL,
    CONSTRAINT installation_data_use_distinct_approvals CHECK (approval_one_id <> approval_two_id),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_intent_sequence bigint NOT NULL UNIQUE CHECK (witness_intent_sequence > 0),
    witness_intent_hash bytea NOT NULL CHECK (octet_length(witness_intent_hash) = 32),
    witness_sequence bigint NOT NULL UNIQUE CHECK (witness_sequence = witness_intent_sequence + 1),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (witness_epoch,witness_sequence),
    UNIQUE (installation_id),
    UNIQUE (activation_plan_sha256)
);

-- Owner-only reconciliation target for one exact independently witnessed writer cutover.
-- The runtime may read this metadata but cannot create, rewrite or delete handoffs.
CREATE TABLE claimcore.writer_handoffs (
    handoff_id uuid PRIMARY KEY CHECK (handoff_id <> '00000000-0000-0000-0000-000000000000'),
    old_generation bigint NOT NULL CHECK (old_generation > 0),
    new_generation bigint NOT NULL CHECK (new_generation = old_generation + 1),
    checkpoint_signing_key_id uuid NOT NULL,
    approval_one_id uuid NOT NULL,
    approval_two_id uuid NOT NULL,
    CONSTRAINT writer_handoff_distinct_approvals CHECK (approval_one_id <> approval_two_id),
    prepare_canonical bytea NOT NULL CHECK (octet_length(prepare_canonical) BETWEEN 1 AND 16384),
    prepare_signature bytea NOT NULL CHECK (octet_length(prepare_signature) = 64),
    prepare_candidate_sha256 bytea NOT NULL CHECK (octet_length(prepare_candidate_sha256) = 32),
    prepare_sequence bigint NOT NULL UNIQUE CHECK (prepare_sequence > 0),
    prepare_hash bytea NOT NULL CHECK (octet_length(prepare_hash) = 32),
    settlement_canonical bytea NOT NULL CHECK (octet_length(settlement_canonical) BETWEEN 1 AND 16384),
    settlement_signature bytea NOT NULL CHECK (octet_length(settlement_signature) = 64),
    settlement_candidate_sha256 bytea NOT NULL CHECK (octet_length(settlement_candidate_sha256) = 32),
    settlement_sequence bigint NOT NULL UNIQUE CHECK (settlement_sequence > prepare_sequence),
    settlement_hash bytea NOT NULL CHECK (octet_length(settlement_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- W1 settlement changes generation but leaves this activation ticket absent. A separate
-- externally verified, witnessed owner act must release the restored writer.
CREATE TABLE claimcore.writer_activations (
    activation_id uuid PRIMARY KEY CHECK (activation_id <> '00000000-0000-0000-0000-000000000000'),
    handoff_id uuid NOT NULL UNIQUE REFERENCES claimcore.writer_handoffs(handoff_id),
    writer_generation bigint NOT NULL CHECK (writer_generation > 1),
    w1_sequence bigint NOT NULL CHECK (w1_sequence > 0),
    w1_hash bytea NOT NULL CHECK (octet_length(w1_hash) = 32),
    checkpoint_signing_key_id uuid NOT NULL,
    checkpoint_holder_actor_id uuid NOT NULL,
    publication_manifest_sha256 bytea NOT NULL CHECK (octet_length(publication_manifest_sha256) = 32),
    report_sha256 bytea NOT NULL CHECK (octet_length(report_sha256) = 32),
    fence_sha256 bytea NOT NULL CHECK (octet_length(fence_sha256) = 32),
    supplement_sha256 bytea NOT NULL CHECK (octet_length(supplement_sha256) = 32),
    final_wal_object_sha256 bytea NOT NULL CHECK (octet_length(final_wal_object_sha256) = 32),
    final_wal_object_count integer NOT NULL CHECK (final_wal_object_count BETWEEN 2 AND 2000),
    independent_probe_sha256 bytea NOT NULL CHECK (octet_length(independent_probe_sha256) = 32),
    probe_evidence_sha256 bytea NOT NULL CHECK (octet_length(probe_evidence_sha256) = 32),
    signed_report bytea NOT NULL CHECK (octet_length(signed_report) BETWEEN 1 AND 4194304),
    report_signature bytea NOT NULL CHECK (octet_length(report_signature) = 64),
    signed_fence bytea NOT NULL CHECK (octet_length(signed_fence) BETWEEN 1 AND 65536),
    fence_signature bytea NOT NULL CHECK (octet_length(fence_signature) = 64),
    signed_supplement bytea NOT NULL CHECK (octet_length(signed_supplement) BETWEEN 1 AND 4194304),
    supplement_signature bytea NOT NULL CHECK (octet_length(supplement_signature) = 64),
    valid_until timestamptz NOT NULL,
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 16384),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_intent_sequence bigint NOT NULL UNIQUE CHECK (witness_intent_sequence > w1_sequence),
    witness_intent_hash bytea NOT NULL CHECK (octet_length(witness_intent_hash) = 32),
    witness_sequence bigint NOT NULL UNIQUE CHECK (witness_sequence = witness_intent_sequence + 1),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (witness_epoch,witness_sequence)
);

-- Two distinct authenticated installation owners approve the exact witnessed handoff
-- candidate; an owner process consumes the approvals with the recorded cutover target.
CREATE TABLE claimcore.writer_handoff_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    handoff_id uuid NOT NULL,
    old_generation bigint NOT NULL CHECK (old_generation > 0),
    expected_witness_sequence bigint NOT NULL CHECK (expected_witness_sequence >= 0),
    expected_witness_hash bytea NOT NULL CHECK (octet_length(expected_witness_hash) = 32),
    new_capability_sha256 bytea NOT NULL CHECK (octet_length(new_capability_sha256) = 32),
    checkpoint_signing_key_id uuid NOT NULL,
    fence_report_sha256 bytea NOT NULL CHECK (octet_length(fence_report_sha256) = 32),
    inventory_sha256 bytea NOT NULL CHECK (octet_length(inventory_sha256) = 32),
    approver_actor_id uuid NOT NULL,
    approver_grant_revision bigint NOT NULL CHECK (approver_grant_revision > 0),
    expires_at timestamptz NOT NULL,
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    UNIQUE (witness_epoch,witness_sequence)
);
CREATE INDEX writer_handoff_approvals_by_handoff
    ON claimcore.writer_handoff_approvals (handoff_id,approval_id);

-- The primary preparation and both one-use approvals co-commit after the independent
-- witness INTENT has atomically fenced all writers. A missing preparation stays unknown.
CREATE TABLE claimcore.writer_handoff_preparations (
    handoff_id uuid PRIMARY KEY CHECK (handoff_id <> '00000000-0000-0000-0000-000000000000'),
    old_generation bigint NOT NULL CHECK (old_generation > 0),
    new_generation bigint NOT NULL CHECK (new_generation = old_generation + 1),
    checkpoint_signing_key_id uuid NOT NULL,
    approval_one_id uuid NOT NULL REFERENCES claimcore.writer_handoff_approvals (approval_id),
    approval_two_id uuid NOT NULL REFERENCES claimcore.writer_handoff_approvals (approval_id),
    CONSTRAINT writer_handoff_preparation_distinct_approvals CHECK (approval_one_id <> approval_two_id),
    reviewed_cutoff_sequence bigint NOT NULL CHECK (reviewed_cutoff_sequence >= 0),
    reviewed_cutoff_hash bytea NOT NULL CHECK (octet_length(reviewed_cutoff_hash) = 32),
    previous_sequence bigint NOT NULL CHECK (previous_sequence >= reviewed_cutoff_sequence),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    new_capability_sha256 bytea NOT NULL CHECK (octet_length(new_capability_sha256) = 32),
    fence_report_sha256 bytea NOT NULL CHECK (octet_length(fence_report_sha256) = 32),
    inventory_sha256 bytea NOT NULL CHECK (octet_length(inventory_sha256) = 32),
    restore_report_sha256 bytea NOT NULL CHECK (octet_length(restore_report_sha256) = 32),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 16384),
    ed25519_signature bytea NOT NULL CHECK (octet_length(ed25519_signature) = 64),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_sequence bigint NOT NULL UNIQUE CHECK (witness_sequence > previous_sequence),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (witness_epoch,witness_sequence)
);

ALTER TABLE claimcore.writer_handoffs
    ADD CONSTRAINT writer_handoff_preparation_fk
    FOREIGN KEY (handoff_id) REFERENCES claimcore.writer_handoff_preparations (handoff_id);

CREATE TABLE claimcore.writer_handoff_approval_uses (
    approval_id uuid PRIMARY KEY REFERENCES claimcore.writer_handoff_approvals (approval_id),
    handoff_id uuid NOT NULL REFERENCES claimcore.writer_handoff_preparations (handoff_id),
    consumed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- A pending handoff can be aborted only by two separate, owner-held abort-purpose
-- signatures over one exact candidate. The witnessed A1 ticket predates these
-- primary rows; the A2 receipt and both one-use links co-commit under owner lock.
CREATE TABLE claimcore.writer_handoff_abort_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    handoff_id uuid NOT NULL REFERENCES claimcore.writer_handoff_preparations (handoff_id),
    signing_key_id uuid NOT NULL,
    owner_actor_id uuid NOT NULL,
    owner_grant_revision bigint NOT NULL CHECK (owner_grant_revision > 0),
    abort_canonical bytea NOT NULL CHECK (octet_length(abort_canonical) BETWEEN 1 AND 16384),
    ed25519_signature bytea NOT NULL CHECK (octet_length(ed25519_signature) = 64),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    expires_at timestamptz NOT NULL,
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    UNIQUE (handoff_id,owner_actor_id),
    UNIQUE (handoff_id,signing_key_id)
);

CREATE TABLE claimcore.writer_handoff_aborts (
    handoff_id uuid PRIMARY KEY REFERENCES claimcore.writer_handoff_preparations (handoff_id),
    old_generation bigint NOT NULL CHECK (old_generation > 0),
    approval_one_id uuid NOT NULL REFERENCES claimcore.writer_handoff_abort_approvals (approval_id),
    approval_two_id uuid NOT NULL REFERENCES claimcore.writer_handoff_abort_approvals (approval_id),
    CONSTRAINT writer_handoff_abort_distinct_approvals CHECK (approval_one_id <> approval_two_id),
    abort_canonical bytea NOT NULL CHECK (octet_length(abort_canonical) BETWEEN 1 AND 16384),
    abort_candidate_sha256 bytea NOT NULL CHECK (octet_length(abort_candidate_sha256) = 32),
    abort_sequence bigint NOT NULL UNIQUE CHECK (abort_sequence > 0),
    abort_hash bytea NOT NULL CHECK (octet_length(abort_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE claimcore.writer_handoff_abort_approval_uses (
    approval_id uuid PRIMARY KEY REFERENCES claimcore.writer_handoff_abort_approvals (approval_id),
    handoff_id uuid NOT NULL REFERENCES claimcore.writer_handoff_aborts (handoff_id),
    consumed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE claimcore.actors (
    actor_id uuid PRIMARY KEY CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
    principal_kind text NOT NULL CHECK (principal_kind IN ('HUMAN', 'SERVICE')),
    issuer text COLLATE "C" NOT NULL CHECK (
        char_length(issuer) BETWEEN 9 AND 2048 AND left(issuer, 8) = 'https://'
        AND issuer = btrim(issuer) AND issuer !~ '[[:cntrl:]]'
    ),
    principal_value text COLLATE "C" NOT NULL CHECK (
        char_length(principal_value) BETWEEN 1 AND 512
        AND principal_value = btrim(principal_value)
        AND principal_value !~ '[[:cntrl:]]'
    ),
    enabled boolean NOT NULL,
    changed_revision bigint NOT NULL CHECK (changed_revision > 0),
    UNIQUE (principal_kind, issuer, principal_value)
);

ALTER TABLE claimcore.writer_handoff_approvals
    ADD CONSTRAINT writer_handoff_approval_actor_fk
    FOREIGN KEY (approver_actor_id) REFERENCES claimcore.actors (actor_id);

CREATE TABLE claimcore.actor_grants (
    actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    scope_kind text NOT NULL CHECK (scope_kind IN ('INSTALLATION', 'CASE')),
    scope_case_id uuid NOT NULL,
    role_name text NOT NULL CHECK (role_name IN (
        'OWNER', 'CASE_READER', 'CASE_EDITOR', 'RECOVERY_OPERATOR',
        'RECOVERY_EXPORTER', 'AUDITOR_CUSTODIAN', 'DATA_STEWARD'
    )),
    active boolean NOT NULL,
    changed_revision bigint NOT NULL CHECK (changed_revision > 0),
    PRIMARY KEY (actor_id, scope_kind, scope_case_id, role_name),
    CONSTRAINT actor_grants_scope CHECK (
        (scope_kind = 'INSTALLATION' AND scope_case_id = '00000000-0000-0000-0000-000000000000')
        OR (scope_kind = 'CASE' AND scope_case_id <> '00000000-0000-0000-0000-000000000000')
    )
);
CREATE INDEX actor_grants_by_case ON claimcore.actor_grants (scope_case_id, actor_id)
    WHERE active AND scope_kind = 'CASE';

-- Every actor/grant change is an exact externally witnessed authority event.
CREATE TABLE claimcore.actor_authority_events (
    revision bigint PRIMARY KEY CHECK (revision > 0),
    event_id uuid NOT NULL UNIQUE CHECK (event_id <> '00000000-0000-0000-0000-000000000000'),
    action_name text NOT NULL CHECK (action_name IN (
        'PROVISION_INITIAL_OWNER', 'REGISTER_ACTOR', 'DISABLE_ACTOR',
        'ENABLE_ACTOR', 'GRANT_ROLE', 'REVOKE_ROLE'
    )),
    target_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    approver_actor_id uuid REFERENCES claimcore.actors(actor_id),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    UNIQUE (witness_epoch, witness_sequence),
    CONSTRAINT actor_authority_events_initial_owner CHECK (
        (action_name = 'PROVISION_INITIAL_OWNER' AND approver_actor_id IS NULL AND revision = 1)
        OR (action_name <> 'PROVISION_INITIAL_OWNER' AND approver_actor_id IS NOT NULL)
    )
);

-- Owner-published, independently source-verified plan bridges private recovery evidence to
-- actor-bound human review without exposing backup paths or credentials to case-work clients.
CREATE TABLE claimcore.installation_data_use_plans (
    plan_id uuid PRIMARY KEY CHECK (plan_id <> '00000000-0000-0000-0000-000000000000'),
    activation_id uuid NOT NULL UNIQUE,
    installation_id uuid NOT NULL,
    lineage_id uuid NOT NULL,
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    writer_generation bigint NOT NULL CHECK (writer_generation > 0),
    policy_sha256 bytea NOT NULL CHECK (octet_length(policy_sha256) = 32),
    plan_sha256 bytea NOT NULL UNIQUE CHECK (octet_length(plan_sha256) = 32),
    canonical_plan bytea NOT NULL CHECK (octet_length(canonical_plan) BETWEEN 1 AND 16384),
    published_at timestamptz NOT NULL,
    witness_intent_sequence bigint NOT NULL UNIQUE CHECK (witness_intent_sequence > 0),
    witness_epoch_at_publication bigint NOT NULL CHECK (witness_epoch_at_publication > 0),
    witness_intent_hash bytea NOT NULL CHECK (octet_length(witness_intent_hash) = 32),
    UNIQUE (plan_id,activation_id)
);

-- Two separately authenticated human installation owners approve the same stable plan;
-- approval authority is witnessed before the owner process can consume it once.
CREATE TABLE claimcore.installation_data_use_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    plan_id uuid NOT NULL REFERENCES claimcore.installation_data_use_plans(plan_id),
    activation_id uuid NOT NULL,
    installation_id uuid NOT NULL,
    lineage_id uuid NOT NULL,
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    writer_generation bigint NOT NULL CHECK (writer_generation > 0),
    activation_plan_sha256 bytea NOT NULL CHECK (octet_length(activation_plan_sha256) = 32),
    policy_sha256 bytea NOT NULL CHECK (octet_length(policy_sha256) = 32),
    review_witness_sequence bigint NOT NULL CHECK (review_witness_sequence >= 0),
    review_witness_hash bytea NOT NULL CHECK (octet_length(review_witness_hash) = 32),
    expected_prior_sequence bigint NOT NULL CHECK (expected_prior_sequence >= 0),
    expected_prior_hash bytea NOT NULL CHECK (octet_length(expected_prior_hash) = 32),
    approver_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    approver_grant_revision bigint NOT NULL CHECK (approver_grant_revision > 0),
    approved_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > approved_at),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > expected_prior_sequence),
    approval_witness_epoch bigint NOT NULL CHECK (approval_witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    UNIQUE (activation_id,approver_actor_id),
    UNIQUE (approval_witness_epoch,witness_sequence),
    FOREIGN KEY (plan_id,activation_id)
        REFERENCES claimcore.installation_data_use_plans(plan_id,activation_id)
);
CREATE INDEX installation_data_use_approvals_by_activation
    ON claimcore.installation_data_use_approvals (activation_id,witness_sequence);

ALTER TABLE claimcore.installation_data_use_activations
    ADD CONSTRAINT installation_data_use_approval_one_fk
    FOREIGN KEY (approval_one_id) REFERENCES claimcore.installation_data_use_approvals(approval_id);
ALTER TABLE claimcore.installation_data_use_activations
    ADD CONSTRAINT installation_data_use_approval_two_fk
    FOREIGN KEY (approval_two_id) REFERENCES claimcore.installation_data_use_approvals(approval_id);

CREATE TABLE claimcore.installation_data_use_approval_uses (
    approval_id uuid PRIMARY KEY REFERENCES claimcore.installation_data_use_approvals(approval_id),
    activation_id uuid NOT NULL REFERENCES claimcore.installation_data_use_activations(activation_id),
    slot integer NOT NULL CHECK (slot IN (1,2)),
    consumed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (activation_id,slot)
);

-- Case disposition, privacy and holds are technical authority outside CaseFields.
-- Every change to these projections is paired with an externally witnessed event.
CREATE TABLE claimcore.case_lifecycle_events (
    event_id uuid PRIMARY KEY CHECK (event_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL,
    case_reference text COLLATE "C" NOT NULL,
    lifecycle_sequence bigint NOT NULL CHECK (lifecycle_sequence > 0),
    business_revision bigint NOT NULL CHECK (business_revision > 0),
    action_name text NOT NULL CHECK (action_name IN (
        'VOID_DATA_ENTRY_ERROR', 'REINSTATE_VOIDED', 'REQUEST_ERASURE',
        'MARK_ERASURE_PENDING', 'RECORD_HOLD', 'RELEASE_HOLD',
        'PURGE_PAYLOAD', 'FINALIZE_ERASURE'
    )),
    actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    grant_revision bigint NOT NULL CHECK (grant_revision > 0),
    draft_sha256 bytea NOT NULL CHECK (octet_length(draft_sha256) = 32),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 16384),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    event_hash bytea NOT NULL CHECK (octet_length(event_hash) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (case_id, lifecycle_sequence),
    UNIQUE (witness_epoch, witness_sequence),
    FOREIGN KEY (case_id, case_reference) REFERENCES claimcore.cases(case_id, case_reference)
);

-- Approvals are separate authenticated, witnessed acts. A stored approver ID is never inferred
-- from a display name, IdP group, or an executor-supplied array at decision time.
CREATE TABLE claimcore.case_lifecycle_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    operation_id uuid NOT NULL CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.cases(case_id),
    action_name text NOT NULL CHECK (action_name IN (
        'VOID_DATA_ENTRY_ERROR', 'REINSTATE_VOIDED', 'PURGE_PAYLOAD', 'FINALIZE_ERASURE'
    )),
    expected_revision bigint NOT NULL CHECK (expected_revision > 0),
    expected_lifecycle_sequence bigint NOT NULL CHECK (expected_lifecycle_sequence >= 0),
    expected_lifecycle_hash bytea NOT NULL CHECK (octet_length(expected_lifecycle_hash) = 32),
    event_digest bytea NOT NULL CHECK (octet_length(event_digest) = 32),
    approver_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    approver_grant_revision bigint NOT NULL CHECK (approver_grant_revision > 0),
    approved_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > approved_at),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    UNIQUE (operation_id, approver_actor_id),
    UNIQUE (witness_epoch, witness_sequence)
);

CREATE TABLE claimcore.case_holds (
    hold_id uuid PRIMARY KEY CHECK (hold_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.cases(case_id),
    ground text NOT NULL CHECK (
        char_length(ground) BETWEEN 1 AND 500 AND ground = btrim(ground)
        AND ground !~ '[[:cntrl:]]'
    ),
    review_on date NOT NULL,
    recorded_by uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    recorded_at timestamptz NOT NULL,
    released_by uuid REFERENCES claimcore.actors(actor_id),
    released_at timestamptz,
    release_reason text CHECK (
        release_reason IS NULL OR (
            char_length(release_reason) BETWEEN 1 AND 500
            AND release_reason = btrim(release_reason)
            AND release_reason !~ '[[:cntrl:]]'
        )
    ),
    CONSTRAINT case_holds_release_group CHECK (
        (released_by IS NULL AND released_at IS NULL AND release_reason IS NULL)
        OR (released_by IS NOT NULL AND released_at IS NOT NULL AND release_reason IS NOT NULL)
    )
);
CREATE INDEX case_holds_active_by_case ON claimcore.case_holds (case_id, hold_id)
    WHERE released_at IS NULL;

-- An erasure request reserves the reference before live case payload is purged.
-- These rows carry keyed, installation-bound commitments, not a raw case reference.
CREATE TABLE claimcore.case_erasure_tombstones (
    case_id uuid PRIMARY KEY CHECK (case_id <> '00000000-0000-0000-0000-000000000000'),
    suppression_key_id uuid NOT NULL CHECK (suppression_key_id <> '00000000-0000-0000-0000-000000000000'),
    reference_commitment bytea NOT NULL CHECK (octet_length(reference_commitment) = 32),
    phase text NOT NULL CHECK (phase IN (
        'ERASURE_REQUESTED', 'ERASURE_PENDING',
        'PAYLOAD_ERASED_SUPPRESSION_RETAINED', 'ERASURE_FINAL'
    )),
    source_revision bigint NOT NULL CHECK (source_revision > 0),
    source_disposition text NOT NULL CHECK (source_disposition IN ('ACTIVE', 'VOIDED_DATA_ENTRY_ERROR')),
    lifecycle_sequence bigint NOT NULL CHECK (lifecycle_sequence > 0),
    lifecycle_hash bytea NOT NULL CHECK (octet_length(lifecycle_hash) = 32),
    denial_count bigint NOT NULL CHECK (denial_count >= 0),
    denial_set_sha256 bytea NOT NULL CHECK (octet_length(denial_set_sha256) = 32),
    request_event_id uuid NOT NULL UNIQUE CHECK (request_event_id <> '00000000-0000-0000-0000-000000000000'),
    request_candidate_sha256 bytea CHECK (
        request_candidate_sha256 IS NULL OR octet_length(request_candidate_sha256) = 32
    ),
    request_candidate_commitment bytea CHECK (
        request_candidate_commitment IS NULL OR octet_length(request_candidate_commitment) = 32
    ),
    request_witness_sequence bigint NOT NULL CHECK (request_witness_sequence > 0),
    request_witness_epoch bigint NOT NULL CHECK (request_witness_epoch > 0),
    request_witness_entry_hash bytea NOT NULL CHECK (octet_length(request_witness_entry_hash) = 32),
    purge_event_id uuid UNIQUE,
    purge_executor_kind text CHECK (
        purge_executor_kind IS NULL OR purge_executor_kind = 'SCHEMA_OWNER_PROCESS'
    ),
    purge_proposal_commitment bytea CHECK (
        purge_proposal_commitment IS NULL OR octet_length(purge_proposal_commitment) = 32
    ),
    purge_valid_until timestamptz,
    purge_source_revision bigint CHECK (purge_source_revision IS NULL OR purge_source_revision > 0),
    purge_lifecycle_sequence bigint CHECK (
        purge_lifecycle_sequence IS NULL OR purge_lifecycle_sequence > 0
    ),
    purge_lifecycle_hash bytea CHECK (
        purge_lifecycle_hash IS NULL OR octet_length(purge_lifecycle_hash) = 32
    ),
    purge_witness_cutoff_sequence bigint CHECK (
        purge_witness_cutoff_sequence IS NULL OR purge_witness_cutoff_sequence >= 0
    ),
    purge_witness_cutoff_hash bytea CHECK (
        purge_witness_cutoff_hash IS NULL OR octet_length(purge_witness_cutoff_hash) = 32
    ),
    purge_subject_intent_count bigint CHECK (
        purge_subject_intent_count IS NULL OR purge_subject_intent_count >= 0
    ),
    purge_subject_intent_sha256 bytea CHECK (
        purge_subject_intent_sha256 IS NULL OR octet_length(purge_subject_intent_sha256) = 32
    ),
    purge_denial_count bigint CHECK (purge_denial_count IS NULL OR purge_denial_count >= 0),
    purge_denial_set_sha256 bytea CHECK (
        purge_denial_set_sha256 IS NULL OR octet_length(purge_denial_set_sha256) = 32
    ),
    purge_copy_inventory_sha256 bytea CHECK (
        purge_copy_inventory_sha256 IS NULL OR octet_length(purge_copy_inventory_sha256) = 32
    ),
    purge_canonical_action bytea CHECK (
        purge_canonical_action IS NULL OR octet_length(purge_canonical_action) BETWEEN 1 AND 16384
    ),
    purge_candidate_sha256 bytea CHECK (purge_candidate_sha256 IS NULL OR octet_length(purge_candidate_sha256) = 32),
    purge_witness_sequence bigint CHECK (purge_witness_sequence IS NULL OR purge_witness_sequence > 0),
    purge_witness_epoch bigint CHECK (purge_witness_epoch IS NULL OR purge_witness_epoch > 0),
    purge_witness_entry_hash bytea CHECK (purge_witness_entry_hash IS NULL OR octet_length(purge_witness_entry_hash) = 32),
    witness_prune_event_id uuid UNIQUE,
    witness_prune_candidate_sha256 bytea CHECK (
        witness_prune_candidate_sha256 IS NULL OR octet_length(witness_prune_candidate_sha256) = 32
    ),
    witness_prune_canonical_action bytea CHECK (
        witness_prune_canonical_action IS NULL OR
        octet_length(witness_prune_canonical_action) BETWEEN 1 AND 16384
    ),
    witness_prune_intent_sequence bigint CHECK (
        witness_prune_intent_sequence IS NULL OR witness_prune_intent_sequence > 0
    ),
    witness_prune_intent_epoch bigint CHECK (
        witness_prune_intent_epoch IS NULL OR witness_prune_intent_epoch > 0
    ),
    witness_prune_intent_hash bytea CHECK (
        witness_prune_intent_hash IS NULL OR octet_length(witness_prune_intent_hash) = 32
    ),
    witness_prune_cutoff_sequence bigint CHECK (
        witness_prune_cutoff_sequence IS NULL OR witness_prune_cutoff_sequence > 0
    ),
    witness_prune_cutoff_hash bytea CHECK (
        witness_prune_cutoff_hash IS NULL OR octet_length(witness_prune_cutoff_hash) = 32
    ),
    witness_prune_target_count bigint CHECK (
        witness_prune_target_count IS NULL OR witness_prune_target_count > 0
    ),
    witness_prune_target_digest bytea CHECK (
        witness_prune_target_digest IS NULL OR octet_length(witness_prune_target_digest) = 32
    ),
    witness_prune_copy_inventory_sha256 bytea CHECK (
        witness_prune_copy_inventory_sha256 IS NULL OR
        octet_length(witness_prune_copy_inventory_sha256) = 32
    ),
    witness_prune_authority_revision bigint CHECK (
        witness_prune_authority_revision IS NULL OR witness_prune_authority_revision >= 0
    ),
    witness_prune_authority_hash bytea CHECK (
        witness_prune_authority_hash IS NULL OR octet_length(witness_prune_authority_hash) = 32
    ),
    witness_prune_valid_until timestamptz,
    copy_absence_event_id uuid UNIQUE,
    suppression_final_event_id uuid UNIQUE,
    live_purged_at timestamptz,
    suppression_until timestamptz,
    retention_policy_id text CHECK (retention_policy_id IS NULL OR (
        char_length(retention_policy_id) BETWEEN 1 AND 128
        AND retention_policy_id = btrim(retention_policy_id)
        AND retention_policy_id !~ '[[:cntrl:]]'
    )),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    identity_coverage text NOT NULL DEFAULT 'PRIMARY_ONLY'
        CHECK (identity_coverage IN ('PRIMARY_ONLY', 'WITNESS_COMPLETE')),
    UNIQUE (suppression_key_id, reference_commitment),
    UNIQUE (case_id, purge_event_id),
    UNIQUE (case_id, witness_prune_event_id),
    UNIQUE (request_witness_epoch, request_witness_sequence),
    CONSTRAINT case_erasure_purge_evidence_group CHECK (
        (purge_event_id IS NULL AND purge_executor_kind IS NULL
         AND purge_proposal_commitment IS NULL
         AND purge_valid_until IS NULL AND purge_source_revision IS NULL
         AND purge_lifecycle_sequence IS NULL AND purge_lifecycle_hash IS NULL
         AND purge_witness_cutoff_sequence IS NULL AND purge_witness_cutoff_hash IS NULL
         AND purge_subject_intent_count IS NULL AND purge_subject_intent_sha256 IS NULL
         AND purge_denial_count IS NULL AND purge_denial_set_sha256 IS NULL
         AND purge_copy_inventory_sha256 IS NULL AND purge_canonical_action IS NULL
         AND purge_candidate_sha256 IS NULL
         AND purge_witness_sequence IS NULL AND purge_witness_epoch IS NULL
         AND purge_witness_entry_hash IS NULL AND live_purged_at IS NULL
         AND identity_coverage = 'PRIMARY_ONLY')
        OR (purge_event_id IS NOT NULL
            AND purge_executor_kind = 'SCHEMA_OWNER_PROCESS'
            AND purge_proposal_commitment IS NOT NULL
            AND purge_valid_until IS NOT NULL AND purge_source_revision IS NOT NULL
            AND purge_lifecycle_sequence IS NOT NULL AND purge_lifecycle_hash IS NOT NULL
            AND purge_witness_cutoff_sequence IS NOT NULL AND purge_witness_cutoff_hash IS NOT NULL
            AND purge_subject_intent_count IS NOT NULL AND purge_subject_intent_sha256 IS NOT NULL
            AND purge_denial_count IS NOT NULL AND purge_denial_set_sha256 IS NOT NULL
            AND purge_copy_inventory_sha256 IS NOT NULL AND purge_canonical_action IS NOT NULL
            AND purge_candidate_sha256 IS NOT NULL
            AND purge_witness_sequence IS NOT NULL AND purge_witness_epoch IS NOT NULL
            AND purge_witness_entry_hash IS NOT NULL AND live_purged_at IS NOT NULL
            AND identity_coverage = 'WITNESS_COMPLETE')
    ),
    CONSTRAINT case_erasure_phase_evidence CHECK (
        phase = 'ERASURE_REQUESTED' OR purge_event_id IS NOT NULL
    ),
    CONSTRAINT case_erasure_request_candidate_protection CHECK (
        (purge_event_id IS NULL AND request_candidate_sha256 IS NOT NULL
         AND request_candidate_commitment IS NULL)
        OR (purge_event_id IS NOT NULL AND request_candidate_sha256 IS NULL
            AND request_candidate_commitment IS NOT NULL)
    ),
    CONSTRAINT case_erasure_purge_order CHECK (
        purge_event_id IS NULL OR (
            purge_valid_until > live_purged_at
            AND purge_source_revision >= source_revision
            AND purge_lifecycle_sequence >= lifecycle_sequence
            AND purge_witness_sequence > purge_witness_cutoff_sequence
            AND purge_candidate_sha256 = sha256(purge_canonical_action)
        )
    ),
    CONSTRAINT case_erasure_witness_prune_group CHECK (
        (witness_prune_event_id IS NULL AND witness_prune_candidate_sha256 IS NULL
         AND witness_prune_canonical_action IS NULL AND witness_prune_intent_sequence IS NULL
         AND witness_prune_intent_epoch IS NULL AND witness_prune_intent_hash IS NULL
         AND witness_prune_cutoff_sequence IS NULL AND witness_prune_cutoff_hash IS NULL
         AND witness_prune_target_count IS NULL AND witness_prune_target_digest IS NULL
         AND witness_prune_copy_inventory_sha256 IS NULL
         AND witness_prune_authority_revision IS NULL AND witness_prune_authority_hash IS NULL
         AND witness_prune_valid_until IS NULL)
        OR (witness_prune_event_id IS NOT NULL AND purge_event_id IS NOT NULL
            AND witness_prune_candidate_sha256 IS NOT NULL
            AND witness_prune_canonical_action IS NOT NULL
            AND witness_prune_intent_sequence IS NOT NULL
            AND witness_prune_intent_epoch IS NOT NULL AND witness_prune_intent_hash IS NOT NULL
            AND witness_prune_cutoff_sequence IS NOT NULL AND witness_prune_cutoff_hash IS NOT NULL
            AND witness_prune_target_count IS NOT NULL AND witness_prune_target_digest IS NOT NULL
            AND witness_prune_copy_inventory_sha256 IS NOT NULL
            AND witness_prune_authority_revision IS NOT NULL
            AND witness_prune_authority_hash IS NOT NULL AND witness_prune_valid_until IS NOT NULL
            AND witness_prune_intent_sequence > witness_prune_cutoff_sequence
            AND witness_prune_candidate_sha256 = sha256(witness_prune_canonical_action))
    ),
    CONSTRAINT case_erasure_terminal_phase CHECK (
        (phase IN ('ERASURE_REQUESTED','ERASURE_PENDING')
            AND copy_absence_event_id IS NULL AND suppression_final_event_id IS NULL)
        OR (phase = 'PAYLOAD_ERASED_SUPPRESSION_RETAINED'
            AND copy_absence_event_id IS NOT NULL AND suppression_final_event_id IS NULL
            AND retention_policy_id IS NOT NULL AND suppression_until IS NOT NULL)
        OR (phase = 'ERASURE_FINAL'
            AND copy_absence_event_id IS NOT NULL AND suppression_final_event_id IS NOT NULL
            AND retention_policy_id IS NOT NULL AND suppression_until IS NOT NULL)
    )
);

CREATE TABLE claimcore.case_erasure_operation_denials (
    operation_commitment bytea PRIMARY KEY CHECK (octet_length(operation_commitment) = 32),
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    knowledge text NOT NULL CHECK (knowledge IN (
        'ACCEPTED', 'REVOKED', 'PENDING', 'ATTEMPT_UNCERTAIN', 'WITNESSED_INTENT'
    )),
    witness_intent_sequence bigint CHECK (
        witness_intent_sequence IS NULL OR witness_intent_sequence > 0
    ),
    witness_intent_epoch bigint CHECK (
        witness_intent_epoch IS NULL OR witness_intent_epoch > 0
    ),
    witness_intent_entry_hash bytea CHECK (
        witness_intent_entry_hash IS NULL OR octet_length(witness_intent_entry_hash) = 32
    ),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT case_erasure_denials_witness_group CHECK (
        (witness_intent_sequence IS NULL AND witness_intent_epoch IS NULL
         AND witness_intent_entry_hash IS NULL)
        OR (witness_intent_sequence IS NOT NULL AND witness_intent_epoch IS NOT NULL
            AND witness_intent_entry_hash IS NOT NULL)
    ),
    UNIQUE (witness_intent_epoch, witness_intent_sequence)
);
CREATE INDEX case_erasure_denials_by_case
    ON claimcore.case_erasure_operation_denials (case_id, operation_commitment);

-- These exact witnessed approvals survive deletion of raw-reference lifecycle drafts. Actor IDs
-- and opaque case IDs remain pseudonymous personal-data risks under the suppression policy.
CREATE TABLE claimcore.case_erasure_purge_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    purge_event_id uuid NOT NULL,
    case_id uuid NOT NULL,
    approver_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    approver_grant_revision bigint NOT NULL CHECK (approver_grant_revision > 0),
    approved_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > approved_at),
    draft_commitment bytea NOT NULL CHECK (octet_length(draft_commitment) = 32),
    approval_commitment bytea NOT NULL CHECK (octet_length(approval_commitment) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    FOREIGN KEY (case_id, purge_event_id)
        REFERENCES claimcore.case_erasure_tombstones(case_id, purge_event_id),
    UNIQUE (purge_event_id, approver_actor_id),
    UNIQUE (witness_epoch, witness_sequence)
);

-- Tombstone-scoped authority survives live claimant-row deletion. Only witnessed hold changes
-- advance this technical tip; approvals bind the current tip without advancing it.
CREATE TABLE claimcore.case_erasure_authority_tip (
    case_id uuid PRIMARY KEY REFERENCES claimcore.case_erasure_tombstones(case_id),
    revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
    event_hash bytea NOT NULL DEFAULT decode(repeat('00',32),'hex')
        CHECK (octet_length(event_hash) = 32)
);

-- Closed codes avoid recreating claimant free text after live purge. Record/release retain
-- separate actor, grant, witness and hash-chain evidence; review dates never auto-release.
CREATE TABLE claimcore.case_erasure_holds (
    hold_id uuid PRIMARY KEY CHECK (hold_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    ground_code text NOT NULL CHECK (ground_code IN (
        'LEGAL_RETENTION','REGULATORY_HOLD','DISPUTE','SECURITY_INCIDENT'
    )),
    review_on date NOT NULL,
    recorded_by uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    recorded_grant_revision bigint NOT NULL CHECK (recorded_grant_revision > 0),
    recorded_at timestamptz NOT NULL,
    record_event_id uuid NOT NULL UNIQUE,
    record_revision bigint NOT NULL CHECK (record_revision > 0),
    record_previous_hash bytea NOT NULL CHECK (octet_length(record_previous_hash) = 32),
    record_event_hash bytea NOT NULL CHECK (octet_length(record_event_hash) = 32),
    record_canonical_action bytea NOT NULL CHECK (octet_length(record_canonical_action) BETWEEN 1 AND 4096),
    record_candidate_sha256 bytea NOT NULL CHECK (octet_length(record_candidate_sha256) = 32),
    record_witness_sequence bigint NOT NULL CHECK (record_witness_sequence > 0),
    record_witness_epoch bigint NOT NULL CHECK (record_witness_epoch > 0),
    record_witness_hash bytea NOT NULL CHECK (octet_length(record_witness_hash) = 32),
    UNIQUE (case_id, record_revision),
    UNIQUE (record_witness_epoch, record_witness_sequence)
);

CREATE TABLE claimcore.case_erasure_hold_releases (
    release_event_id uuid PRIMARY KEY CHECK (release_event_id <> '00000000-0000-0000-0000-000000000000'),
    hold_id uuid NOT NULL UNIQUE REFERENCES claimcore.case_erasure_holds(hold_id),
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    release_code text NOT NULL CHECK (release_code IN (
        'LEGAL_RELEASE','REVIEW_CLOSED','EXPIRED_WITH_REVIEW'
    )),
    released_by uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    released_grant_revision bigint NOT NULL CHECK (released_grant_revision > 0),
    released_at timestamptz NOT NULL,
    release_revision bigint NOT NULL CHECK (release_revision > 0),
    release_previous_hash bytea NOT NULL CHECK (octet_length(release_previous_hash) = 32),
    release_event_hash bytea NOT NULL CHECK (octet_length(release_event_hash) = 32),
    release_canonical_action bytea NOT NULL CHECK (
        octet_length(release_canonical_action) BETWEEN 1 AND 4096
    ),
    release_candidate_sha256 bytea NOT NULL CHECK (octet_length(release_candidate_sha256) = 32),
    release_witness_sequence bigint NOT NULL CHECK (release_witness_sequence > 0),
    release_witness_epoch bigint NOT NULL CHECK (release_witness_epoch > 0),
    release_witness_hash bytea NOT NULL CHECK (octet_length(release_witness_hash) = 32),
    UNIQUE (case_id,release_revision),
    UNIQUE (release_witness_epoch,release_witness_sequence)
);
CREATE INDEX case_erasure_holds_by_case ON claimcore.case_erasure_holds(case_id,hold_id);
CREATE INDEX case_erasure_hold_releases_by_case
    ON claimcore.case_erasure_hold_releases(case_id,hold_id);

CREATE TABLE claimcore.case_erasure_prune_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    prune_event_id uuid NOT NULL CHECK (prune_event_id <> '00000000-0000-0000-0000-000000000000'),
    purge_event_id uuid NOT NULL,
    cutoff_sequence bigint NOT NULL CHECK (cutoff_sequence > 0),
    cutoff_hash bytea NOT NULL CHECK (octet_length(cutoff_hash) = 32),
    target_count bigint NOT NULL CHECK (target_count > 0),
    target_digest bytea NOT NULL CHECK (octet_length(target_digest) = 32),
    expected_authority_revision bigint NOT NULL CHECK (expected_authority_revision >= 0),
    expected_authority_hash bytea NOT NULL CHECK (octet_length(expected_authority_hash) = 32),
    valid_until timestamptz NOT NULL,
    approver_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    approver_grant_revision bigint NOT NULL CHECK (approver_grant_revision > 0),
    approved_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > approved_at),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    FOREIGN KEY (case_id,purge_event_id)
        REFERENCES claimcore.case_erasure_tombstones(case_id,purge_event_id),
    UNIQUE (prune_event_id,approver_actor_id),
    UNIQUE (witness_epoch,witness_sequence)
);

CREATE TABLE claimcore.case_erasure_prune_targets (
    case_id uuid NOT NULL,
    prune_event_id uuid NOT NULL,
    sequence bigint NOT NULL CHECK (sequence > 0),
    operation_id uuid NOT NULL CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    phase text NOT NULL CHECK (phase IN (
        'INTENT','SETTLED_ACCEPTED','SETTLED_REVOKED','SETTLED_AUTHORITY','ABORTED_BEFORE_COMMIT'
    )),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    entry_hash bytea NOT NULL CHECK (octet_length(entry_hash) = 32),
    payload_sha256 bytea NOT NULL CHECK (octet_length(payload_sha256) = 32),
    is_external_publication boolean NOT NULL,
    PRIMARY KEY (case_id,sequence),
    FOREIGN KEY (case_id,prune_event_id)
        REFERENCES claimcore.case_erasure_tombstones(case_id,witness_prune_event_id)
);
CREATE INDEX case_erasure_prune_targets_by_event
    ON claimcore.case_erasure_prune_targets(prune_event_id,sequence);

-- A steward's exact terminal draft approval is authority, not evidence that a location is
-- absent. The schema-owner re-verifies signed evidence and consumes two distinct approvals.
CREATE TABLE claimcore.case_erasure_terminal_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    terminal_event_id uuid NOT NULL CHECK (
        terminal_event_id <> '00000000-0000-0000-0000-000000000000'
    ),
    case_id uuid NOT NULL,
    action_name text NOT NULL CHECK (action_name IN (
        'CONFIRM_MANAGED_PAYLOAD_ABSENCE','COMPLETE_SUPPRESSION_HORIZON'
    )),
    prune_event_id uuid NOT NULL,
    expected_authority_revision bigint NOT NULL CHECK (expected_authority_revision >= 0),
    expected_authority_hash bytea NOT NULL CHECK (octet_length(expected_authority_hash) = 32),
    installation_id uuid NOT NULL,
    lineage_id uuid NOT NULL,
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_cutoff_sequence bigint NOT NULL CHECK (witness_cutoff_sequence > 0),
    witness_cutoff_hash bytea NOT NULL CHECK (octet_length(witness_cutoff_hash) = 32),
    copy_inventory_digest bytea NOT NULL CHECK (octet_length(copy_inventory_digest) = 32),
    relevant_copy_count bigint NOT NULL CHECK (relevant_copy_count >= 0),
    expected_writer_generation bigint NOT NULL CHECK (expected_writer_generation > 0),
    recovery_fence_digest bytea CHECK (
        recovery_fence_digest IS NULL OR octet_length(recovery_fence_digest) = 32
    ),
    old_writer_generation bigint CHECK (old_writer_generation IS NULL OR old_writer_generation > 0),
    new_writer_generation bigint CHECK (new_writer_generation IS NULL OR new_writer_generation > 0),
    policy_id text NOT NULL CHECK (
        char_length(policy_id) BETWEEN 1 AND 128
        AND policy_id = btrim(policy_id) AND policy_id !~ '[[:cntrl:]]'
    ),
    suppression_until timestamptz NOT NULL,
    valid_until timestamptz NOT NULL,
    approver_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    approver_grant_revision bigint NOT NULL CHECK (approver_grant_revision > 0),
    approved_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > approved_at),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    approval_witness_epoch bigint NOT NULL CHECK (approval_witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    FOREIGN KEY (case_id,prune_event_id)
        REFERENCES claimcore.case_erasure_tombstones(case_id,witness_prune_event_id),
    UNIQUE (terminal_event_id,approver_actor_id),
    UNIQUE (case_id,approval_id),
    UNIQUE (approval_witness_epoch,witness_sequence),
    CONSTRAINT case_erasure_terminal_approval_shape CHECK (
        (action_name = 'CONFIRM_MANAGED_PAYLOAD_ABSENCE'
            AND recovery_fence_digest IS NULL
            AND old_writer_generation IS NULL AND new_writer_generation IS NULL)
        OR (action_name = 'COMPLETE_SUPPRESSION_HORIZON'
            AND recovery_fence_digest IS NOT NULL
            AND old_writer_generation IS NOT NULL AND new_writer_generation IS NOT NULL
            AND old_writer_generation < 9223372036854775807
            AND new_writer_generation = old_writer_generation + 1
            AND expected_writer_generation = new_writer_generation)
    ),
    CONSTRAINT case_erasure_terminal_approval_time CHECK (
        valid_until > approved_at
        AND valid_until <= approved_at + interval '24 hours'
        AND expires_at <= valid_until
    )
);
CREATE INDEX case_erasure_terminal_approvals_by_event
    ON claimcore.case_erasure_terminal_approvals(terminal_event_id,approver_actor_id);

-- The owner process is the technical executor. These append-only receipts retain only
-- pseudonymous authority, copy and recovery-fence evidence after claimant payload is gone.
CREATE TABLE claimcore.case_erasure_terminal_events (
    terminal_event_id uuid PRIMARY KEY CHECK (
        terminal_event_id <> '00000000-0000-0000-0000-000000000000'
    ),
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    action_name text NOT NULL CHECK (action_name IN (
        'CONFIRM_MANAGED_PAYLOAD_ABSENCE','COMPLETE_SUPPRESSION_HORIZON'
    )),
    resulting_phase text NOT NULL CHECK (resulting_phase IN (
        'PAYLOAD_ERASED_SUPPRESSION_RETAINED','ERASURE_FINAL'
    )),
    executor_kind text NOT NULL CHECK (executor_kind = 'SCHEMA_OWNER_PROCESS'),
    policy_id text NOT NULL CHECK (
        char_length(policy_id) BETWEEN 1 AND 128
        AND policy_id = btrim(policy_id) AND policy_id !~ '[[:cntrl:]]'
    ),
    suppression_until timestamptz NOT NULL,
    prune_event_id uuid NOT NULL,
    copy_inventory_digest bytea NOT NULL CHECK (octet_length(copy_inventory_digest) = 32),
    relevant_copy_count bigint NOT NULL CHECK (relevant_copy_count >= 0),
    writer_generation bigint NOT NULL CHECK (writer_generation > 0),
    copy_absence_seal_sha256 bytea NOT NULL CHECK (octet_length(copy_absence_seal_sha256) = 32),
    recovery_fence_digest bytea CHECK (
        recovery_fence_digest IS NULL OR octet_length(recovery_fence_digest) = 32
    ),
    approval_one_id uuid NOT NULL,
    approval_two_id uuid NOT NULL,
    actor_authority_revision bigint NOT NULL CHECK (actor_authority_revision > 0),
    previous_authority_revision bigint NOT NULL CHECK (previous_authority_revision >= 0),
    previous_authority_hash bytea NOT NULL CHECK (octet_length(previous_authority_hash) = 32),
    authority_revision bigint NOT NULL CHECK (authority_revision = previous_authority_revision + 1),
    authority_hash bytea NOT NULL CHECK (octet_length(authority_hash) = 32),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 16384),
    candidate_sha256 bytea NOT NULL CHECK (
        octet_length(candidate_sha256) = 32 AND candidate_sha256 = sha256(canonical_action)
    ),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    recorded_at timestamptz NOT NULL,
    UNIQUE (case_id,terminal_event_id),
    UNIQUE (case_id,action_name),
    UNIQUE (case_id,authority_revision),
    UNIQUE (witness_epoch,witness_sequence),
    CHECK (approval_one_id <> approval_two_id),
    CONSTRAINT case_erasure_terminal_event_phase CHECK (
        (action_name = 'CONFIRM_MANAGED_PAYLOAD_ABSENCE'
            AND resulting_phase = 'PAYLOAD_ERASED_SUPPRESSION_RETAINED'
            AND recovery_fence_digest IS NULL)
        OR (action_name = 'COMPLETE_SUPPRESSION_HORIZON'
            AND resulting_phase = 'ERASURE_FINAL'
            AND recovery_fence_digest IS NOT NULL)
    ),
    FOREIGN KEY (case_id,prune_event_id)
        REFERENCES claimcore.case_erasure_tombstones(case_id,witness_prune_event_id),
    FOREIGN KEY (case_id,approval_one_id)
        REFERENCES claimcore.case_erasure_terminal_approvals(case_id,approval_id),
    FOREIGN KEY (case_id,approval_two_id)
        REFERENCES claimcore.case_erasure_terminal_approvals(case_id,approval_id)
);

CREATE TABLE claimcore.case_erasure_terminal_approval_uses (
    approval_id uuid PRIMARY KEY,
    terminal_event_id uuid NOT NULL,
    case_id uuid NOT NULL,
    slot integer NOT NULL CHECK (slot IN (1,2)),
    used_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (terminal_event_id,slot),
    FOREIGN KEY (case_id,approval_id)
        REFERENCES claimcore.case_erasure_terminal_approvals(case_id,approval_id),
    FOREIGN KEY (case_id,terminal_event_id)
        REFERENCES claimcore.case_erasure_terminal_events(case_id,terminal_event_id)
);

ALTER TABLE claimcore.case_erasure_tombstones
    ADD CONSTRAINT case_erasure_copy_absence_event_fk
    FOREIGN KEY (case_id,copy_absence_event_id)
    REFERENCES claimcore.case_erasure_terminal_events(case_id,terminal_event_id);
ALTER TABLE claimcore.case_erasure_tombstones
    ADD CONSTRAINT case_erasure_suppression_final_event_fk
    FOREIGN KEY (case_id,suppression_final_event_id)
    REFERENCES claimcore.case_erasure_terminal_events(case_id,terminal_event_id);

-- Export receipts are immutable and payload-free. The witness settlement is read
-- independently; an intent-only ticket never proves an export.
CREATE TABLE claimcore.recovery_artifact_exports (
    export_id uuid PRIMARY KEY CHECK (export_id <> '00000000-0000-0000-0000-000000000000'),
    installation_id uuid NOT NULL,
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    case_id uuid NOT NULL,
    operation_id uuid NOT NULL,
    preparer_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    preparer_grant_revision bigint NOT NULL CHECK (preparer_grant_revision >= 0),
    importer_actor_id uuid NULL REFERENCES claimcore.actors(actor_id),
    exporter_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    exporter_grant_revision bigint NOT NULL CHECK (exporter_grant_revision >= 0),
    key_id uuid NOT NULL CHECK (key_id <> '00000000-0000-0000-0000-000000000000'),
    nonce bytea NOT NULL CHECK (octet_length(nonce) = 12),
    issued_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (
        expires_at > issued_at AND expires_at <= issued_at + interval '7 days'
    ),
    artifact_sha256 bytea NOT NULL CHECK (octet_length(artifact_sha256) = 32),
    key_issuance_ordinal integer NOT NULL CHECK (key_issuance_ordinal BETWEEN 1 AND 65536),
    key_max_exports integer NOT NULL CHECK (
        key_max_exports BETWEEN 1 AND 65536 AND key_issuance_ordinal <= key_max_exports
    ),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    witness_candidate_sha256 bytea NOT NULL CHECK (octet_length(witness_candidate_sha256) = 32),
    UNIQUE (key_id, nonce),
    UNIQUE (witness_epoch, witness_sequence)
);
CREATE INDEX recovery_artifact_exports_by_key
    ON claimcore.recovery_artifact_exports (key_id, issued_at, export_id);

-- Payload custody is separate from the immutable export receipt. Authorized erasure can
-- remove these encrypted bytes while retaining the witnessed nonce and managed-copy ledger.
CREATE TABLE claimcore.recovery_artifact_payloads (
    export_id uuid PRIMARY KEY REFERENCES claimcore.recovery_artifact_exports(export_id),
    artifact_bytes bytea NOT NULL CHECK (octet_length(artifact_bytes) BETWEEN 1 AND 200704),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 1040000)
);

-- Trusted Ed25519 backup signer keys are registered by separate witnessed dual-human
-- authority. An incoming copy attestation cannot introduce its own trust anchor.
CREATE TABLE claimcore.managed_copy_signers (
    signing_key_id uuid PRIMARY KEY CHECK (signing_key_id <> '00000000-0000-0000-0000-000000000000'),
    signer_purpose text NOT NULL CHECK (signer_purpose IN (
        'COPY_ATTESTOR', 'LOCATION_REGISTRY', 'LOCATION_INSPECTOR',
        'DELETION_VERIFIER', 'RESTORE_REPORT', 'CHECKPOINT',
        'WRITER_HANDOFF_ABORT', 'RESTORE_COPY_VERIFIER',
        'INSTALLATION_LOSS_RETIREMENT'
    )),
    holder_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    ed25519_public_key bytea NOT NULL CHECK (octet_length(ed25519_public_key) = 32),
    public_key_sha256 bytea NOT NULL CHECK (octet_length(public_key_sha256) = 32),
    active boolean NOT NULL,
    revision bigint NOT NULL CHECK (revision > 0),
    event_hash bytea NOT NULL CHECK (octet_length(event_hash) = 32),
    UNIQUE (public_key_sha256)
);

ALTER TABLE claimcore.writer_handoff_abort_approvals
    ADD CONSTRAINT writer_handoff_abort_approval_signer_fk
    FOREIGN KEY (signing_key_id) REFERENCES claimcore.managed_copy_signers (signing_key_id);
ALTER TABLE claimcore.writer_handoff_abort_approvals
    ADD CONSTRAINT writer_handoff_abort_approval_actor_fk
    FOREIGN KEY (owner_actor_id) REFERENCES claimcore.actors (actor_id);

-- Each owner or custodian approval is an independently authenticated, witnessed
-- authority action. A schema-owner command consumes two distinct exact approvals;
-- it cannot substitute caller-supplied actor IDs for this retained evidence.
CREATE TABLE claimcore.managed_copy_signer_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    signing_key_id uuid NOT NULL CHECK (signing_key_id <> '00000000-0000-0000-0000-000000000000'),
    action_name text NOT NULL CHECK (action_name IN ('REGISTER', 'RETIRE')),
    signer_purpose text NOT NULL CHECK (signer_purpose IN (
        'COPY_ATTESTOR', 'LOCATION_REGISTRY', 'LOCATION_INSPECTOR',
        'DELETION_VERIFIER', 'RESTORE_REPORT', 'CHECKPOINT',
        'WRITER_HANDOFF_ABORT', 'RESTORE_COPY_VERIFIER',
        'INSTALLATION_LOSS_RETIREMENT'
    )),
    holder_approval_id uuid REFERENCES claimcore.managed_copy_signer_approvals(approval_id),
    public_key_sha256 bytea NOT NULL CHECK (octet_length(public_key_sha256) = 32),
    actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    actor_role text NOT NULL CHECK (actor_role IN ('OWNER', 'AUDITOR_CUSTODIAN', 'DATA_STEWARD')),
    CONSTRAINT managed_copy_signer_approvals_holder_shape CHECK (
        (actor_role = 'OWNER' AND holder_approval_id IS NOT NULL)
        OR (actor_role <> 'OWNER' AND holder_approval_id IS NULL)
    ),
    grant_revision bigint NOT NULL CHECK (grant_revision > 0),
    expires_at timestamptz NOT NULL,
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (witness_epoch, witness_sequence),
    CHECK (expires_at > recorded_at)
);
CREATE INDEX managed_copy_signer_approvals_target
    ON claimcore.managed_copy_signer_approvals (signing_key_id, action_name, expires_at);

CREATE TABLE claimcore.managed_copy_signer_events (
    event_id uuid PRIMARY KEY CHECK (event_id <> '00000000-0000-0000-0000-000000000000'),
    signing_key_id uuid NOT NULL REFERENCES claimcore.managed_copy_signers(signing_key_id),
    revision bigint NOT NULL CHECK (revision > 0),
    action_name text NOT NULL CHECK (action_name IN ('REGISTER', 'RETIRE')),
    signer_purpose text NOT NULL CHECK (signer_purpose IN (
        'COPY_ATTESTOR', 'LOCATION_REGISTRY', 'LOCATION_INSPECTOR',
        'DELETION_VERIFIER', 'RESTORE_REPORT', 'CHECKPOINT',
        'WRITER_HANDOFF_ABORT', 'RESTORE_COPY_VERIFIER',
        'INSTALLATION_LOSS_RETIREMENT'
    )),
    holder_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    owner_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    custodian_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    owner_approval_id uuid NOT NULL REFERENCES claimcore.managed_copy_signer_approvals(approval_id),
    custodian_approval_id uuid NOT NULL REFERENCES claimcore.managed_copy_signer_approvals(approval_id),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    event_hash bytea NOT NULL CHECK (octet_length(event_hash) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (signing_key_id, revision),
    UNIQUE (witness_epoch, witness_sequence),
    CHECK (owner_actor_id <> custodian_actor_id AND holder_actor_id = custodian_actor_id),
    CHECK (owner_approval_id <> custodian_approval_id),
    CHECK ((revision = 1 AND action_name = 'REGISTER')
        OR (revision > 1 AND action_name = 'RETIRE'))
);

CREATE TABLE claimcore.managed_copy_signer_approval_uses (
    approval_id uuid PRIMARY KEY REFERENCES claimcore.managed_copy_signer_approvals(approval_id),
    signer_event_id uuid NOT NULL REFERENCES claimcore.managed_copy_signer_events(event_id),
    actor_role text NOT NULL CHECK (actor_role IN ('OWNER', 'CUSTODIAN')),
    used_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (signer_event_id, actor_role)
);

-- A terminal loss decision never repairs missing evidence or resumes this installation.
-- The two owner signatures bind a private, data-minimal decision; individual known operation
-- identities are retained only as keyed commitments outside the signed document.
CREATE TABLE claimcore.installation_loss_retirements (
    retirement_id uuid PRIMARY KEY CHECK (retirement_id <> '00000000-0000-0000-0000-000000000000'),
    installation_id uuid NOT NULL,
    lineage_id uuid NOT NULL,
    old_epoch bigint NOT NULL CHECK (old_epoch > 0),
    previous_sequence bigint NOT NULL CHECK (previous_sequence >= 0),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    evidence_report_sha256 bytea CHECK (evidence_report_sha256 IS NULL OR octet_length(evidence_report_sha256) = 32),
    independent_checkpoint_sha256 bytea CHECK (independent_checkpoint_sha256 IS NULL OR octet_length(independent_checkpoint_sha256) = 32),
    operation_set_kind text NOT NULL CHECK (operation_set_kind IN ('KNOWN_OPERATIONS','UNKNOWN_OPERATIONS')),
    known_operation_count integer NOT NULL CHECK (known_operation_count BETWEEN 0 AND 10000),
    known_operation_digest bytea NOT NULL CHECK (octet_length(known_operation_digest) = 32),
    signer_one_id uuid NOT NULL REFERENCES claimcore.managed_copy_signers(signing_key_id),
    signer_two_id uuid NOT NULL REFERENCES claimcore.managed_copy_signers(signing_key_id),
    owner_one_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    owner_two_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    owner_one_grant_revision bigint NOT NULL CHECK (owner_one_grant_revision > 0),
    owner_two_grant_revision bigint NOT NULL CHECK (owner_two_grant_revision > 0),
    authority_revision bigint NOT NULL CHECK (authority_revision > 0),
    valid_until timestamptz NOT NULL,
    canonical_decision bytea NOT NULL CHECK (octet_length(canonical_decision) BETWEEN 1 AND 16384),
    canonical_sha256 bytea NOT NULL CHECK (octet_length(canonical_sha256) = 32),
    signature_one bytea NOT NULL CHECK (octet_length(signature_one) = 64),
    signature_two bytea NOT NULL CHECK (octet_length(signature_two) = 64),
    witness_intent_sequence bigint NOT NULL UNIQUE CHECK (witness_intent_sequence > previous_sequence),
    witness_intent_hash bytea NOT NULL CHECK (octet_length(witness_intent_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (installation_id, lineage_id),
    CONSTRAINT loss_retirement_distinct_owners CHECK (
        signer_one_id <> signer_two_id AND owner_one_actor_id <> owner_two_actor_id
    ),
    CONSTRAINT loss_retirement_unknown_set CHECK (
        operation_set_kind <> 'UNKNOWN_OPERATIONS' OR known_operation_count = 0
    )
);

CREATE TABLE claimcore.installation_loss_operation_denials (
    operation_commitment bytea PRIMARY KEY CHECK (octet_length(operation_commitment) = 32),
    retirement_id uuid NOT NULL REFERENCES claimcore.installation_loss_retirements(retirement_id)
);

ALTER TABLE claimcore.installation_lineage
    ADD CONSTRAINT installation_loss_retirement_fk
    FOREIGN KEY (loss_retirement_id) REFERENCES claimcore.installation_loss_retirements(retirement_id);

-- Current projection and immutable event chain for every managed backup, WAL copy,
-- snapshot, replica, key copy and product export. UNKNOWN never means deleted.
CREATE TABLE claimcore.managed_copies (
    copy_id uuid PRIMARY KEY CHECK (copy_id <> '00000000-0000-0000-0000-000000000000'),
    installation_id uuid NOT NULL,
    lineage_id uuid NOT NULL,
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    producer_kind text NOT NULL CHECK (producer_kind IN (
        'OWNER_ATTESTED', 'PRODUCT_EXPORT', 'ADOPTED_EXTERNAL'
    )),
    cluster_name text NOT NULL CHECK (cluster_name IN ('PRIMARY', 'WITNESS', 'NONE')),
    copy_kind text NOT NULL CHECK (copy_kind IN (
        'BASE', 'WAL', 'WITNESS_PAYLOAD', 'SNAPSHOT', 'REPLICA', 'EXPORT', 'ENCRYPTION_KEY_COPY'
    )),
    source_case_id uuid,
    postgres_system_id text CHECK (postgres_system_id IS NULL OR postgres_system_id ~ '^[0-9]{1,20}$'),
    timeline integer CHECK (timeline IS NULL OR timeline > 0),
    wal_segment_bytes integer CHECK (
        wal_segment_bytes IS NULL OR (
            wal_segment_bytes BETWEEN 1048576 AND 1073741824
            AND (wal_segment_bytes & (wal_segment_bytes - 1)) = 0
        )
    ),
    backup_manifest_sha256 bytea CHECK (backup_manifest_sha256 IS NULL OR octet_length(backup_manifest_sha256) = 32),
    wal_start_lsn text CHECK (wal_start_lsn IS NULL OR wal_start_lsn ~ '^[0-9A-F]{1,8}/[0-9A-F]{1,8}$'),
    wal_end_lsn text CHECK (wal_end_lsn IS NULL OR wal_end_lsn ~ '^[0-9A-F]{1,8}/[0-9A-F]{1,8}$'),
    wal_segment text CHECK (wal_segment IS NULL OR wal_segment ~ '^([0-9A-F]{24}|[0-9A-F]{8}\.history)$'),
    witness_cutoff_sequence bigint CHECK (witness_cutoff_sequence IS NULL OR witness_cutoff_sequence >= 0),
    witness_cutoff_hash bytea CHECK (witness_cutoff_hash IS NULL OR octet_length(witness_cutoff_hash) = 32),
    ciphertext_sha256 bytea NOT NULL CHECK (octet_length(ciphertext_sha256) = 32),
    ciphertext_bytes bigint NOT NULL CHECK (ciphertext_bytes > 0),
    encryption_key_id uuid NOT NULL CHECK (encryption_key_id <> '00000000-0000-0000-0000-000000000000'),
    signing_key_id uuid REFERENCES claimcore.managed_copy_signers(signing_key_id),
    custodian_commitment bytea CHECK (custodian_commitment IS NULL OR octet_length(custodian_commitment) = 32),
    location_commitment bytea CHECK (location_commitment IS NULL OR octet_length(location_commitment) = 32),
    captured_at timestamptz NOT NULL,
    retain_until timestamptz NOT NULL CHECK (retain_until > captured_at),
    last_verified_at timestamptz,
    verification_proof_sha256 bytea CHECK (verification_proof_sha256 IS NULL OR octet_length(verification_proof_sha256) = 32),
    deletion_proof_sha256 bytea CHECK (deletion_proof_sha256 IS NULL OR octet_length(deletion_proof_sha256) = 32),
    state text NOT NULL CHECK (state IN ('UNVERIFIED', 'RETAINED', 'DELETE_PENDING', 'VERIFIED_DELETED', 'UNKNOWN')),
    revision bigint NOT NULL CHECK (revision > 0),
    event_hash bytea NOT NULL CHECK (octet_length(event_hash) = 32),
    product_export_id uuid UNIQUE REFERENCES claimcore.recovery_artifact_exports(export_id),
    UNIQUE (copy_id,source_case_id),
    CONSTRAINT managed_copies_product_source CHECK (
        (producer_kind = 'PRODUCT_EXPORT' AND copy_kind = 'EXPORT' AND cluster_name = 'NONE'
            AND product_export_id IS NOT NULL AND product_export_id = copy_id
            AND source_case_id IS NOT NULL
            AND signing_key_id IS NULL AND custodian_commitment IS NULL
            AND location_commitment IS NULL
            AND witness_cutoff_sequence IS NULL AND witness_cutoff_hash IS NULL)
        OR (producer_kind = 'OWNER_ATTESTED' AND product_export_id IS NULL AND signing_key_id IS NOT NULL
            AND custodian_commitment IS NOT NULL AND location_commitment IS NOT NULL
            AND witness_cutoff_sequence IS NOT NULL AND witness_cutoff_hash IS NOT NULL)
        OR (producer_kind = 'ADOPTED_EXTERNAL' AND product_export_id IS NULL
            AND copy_kind = 'EXPORT' AND cluster_name = 'NONE' AND source_case_id IS NOT NULL
            AND signing_key_id IS NOT NULL AND custodian_commitment IS NOT NULL
            AND location_commitment IS NOT NULL
            AND witness_cutoff_sequence IS NOT NULL AND witness_cutoff_hash IS NOT NULL)
    ),
    CONSTRAINT managed_copies_postgres_kind CHECK (
        (copy_kind NOT IN ('BASE', 'WAL', 'SNAPSHOT', 'REPLICA')) OR
        (cluster_name IN ('PRIMARY', 'WITNESS') AND postgres_system_id IS NOT NULL
            AND timeline IS NOT NULL AND wal_segment_bytes IS NOT NULL)
    ),
    CONSTRAINT managed_copies_non_postgres_kind CHECK (
        copy_kind IN ('BASE', 'WAL', 'SNAPSHOT', 'REPLICA') OR
        (postgres_system_id IS NULL AND timeline IS NULL AND wal_segment_bytes IS NULL)
    ),
    CONSTRAINT managed_copies_case_kind CHECK (
        producer_kind <> 'OWNER_ATTESTED' OR
        ((copy_kind IN ('BASE', 'WAL', 'SNAPSHOT', 'REPLICA')
            AND cluster_name IN ('PRIMARY', 'WITNESS') AND source_case_id IS NULL)
         OR (copy_kind = 'WITNESS_PAYLOAD' AND cluster_name = 'WITNESS'
            AND source_case_id IS NOT NULL)
         OR (copy_kind = 'EXPORT' AND cluster_name = 'NONE'
            AND source_case_id IS NOT NULL)
         OR (copy_kind = 'ENCRYPTION_KEY_COPY' AND cluster_name = 'NONE'
            AND source_case_id IS NULL))
    ),
    CONSTRAINT managed_copies_base_wal_shape CHECK (
        (copy_kind <> 'BASE' OR (backup_manifest_sha256 IS NOT NULL
            AND wal_start_lsn IS NOT NULL AND wal_end_lsn IS NOT NULL AND wal_segment IS NULL))
        AND (copy_kind <> 'WAL' OR (wal_segment IS NOT NULL
            AND backup_manifest_sha256 IS NULL AND wal_start_lsn IS NULL AND wal_end_lsn IS NULL))
        AND (copy_kind IN ('BASE', 'WAL') OR
            (backup_manifest_sha256 IS NULL AND wal_start_lsn IS NULL
             AND wal_end_lsn IS NULL AND wal_segment IS NULL))
    ),
    CONSTRAINT managed_copies_initial_state CHECK (
        revision > 1 OR (producer_kind = 'PRODUCT_EXPORT' AND state = 'UNKNOWN')
        OR (producer_kind = 'OWNER_ATTESTED' AND state = 'UNVERIFIED')
        OR (producer_kind = 'ADOPTED_EXTERNAL' AND state = 'UNKNOWN')
    ),
    CONSTRAINT managed_copies_deletion_proof CHECK (
        state <> 'VERIFIED_DELETED' OR
        (deletion_proof_sha256 IS NOT NULL AND last_verified_at IS NOT NULL)
    ),
    CONSTRAINT managed_copies_verification_proof CHECK (
        (state = 'UNVERIFIED' AND verification_proof_sha256 IS NULL AND last_verified_at IS NULL)
        OR (state = 'RETAINED'
            AND verification_proof_sha256 IS NOT NULL AND last_verified_at IS NOT NULL)
        OR (state IN ('DELETE_PENDING', 'VERIFIED_DELETED')
            AND (verification_proof_sha256 IS NULL OR last_verified_at IS NOT NULL))
        OR state = 'UNKNOWN'
    )
);

CREATE TABLE claimcore.managed_copy_events (
    event_id uuid PRIMARY KEY CHECK (event_id <> '00000000-0000-0000-0000-000000000000'),
    copy_id uuid NOT NULL REFERENCES claimcore.managed_copies(copy_id),
    revision bigint NOT NULL CHECK (revision > 0),
    event_kind text NOT NULL CHECK (event_kind IN (
        'REGISTER', 'ADOPT', 'VERIFY', 'DELETE_REQUEST', 'VERIFIED_DELETED', 'UNKNOWN'
    )),
    producer_kind text NOT NULL CHECK (producer_kind IN (
        'OWNER_ATTESTED', 'PRODUCT_EXPORT', 'ADOPTED_EXTERNAL'
    )),
    canonical_attestation bytea NOT NULL CHECK (octet_length(canonical_attestation) BETWEEN 1 AND 300000),
    signing_key_id uuid REFERENCES claimcore.managed_copy_signers(signing_key_id),
    ed25519_signature bytea CHECK (ed25519_signature IS NULL OR octet_length(ed25519_signature) = 64),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    event_hash bytea NOT NULL CHECK (octet_length(event_hash) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (copy_id, revision),
    UNIQUE (event_id,copy_id),
    UNIQUE (witness_epoch, witness_sequence),
    CONSTRAINT managed_copy_events_signature CHECK (
        (producer_kind = 'OWNER_ATTESTED' AND event_kind <> 'ADOPT'
            AND signing_key_id IS NOT NULL AND ed25519_signature IS NOT NULL)
        OR (producer_kind = 'PRODUCT_EXPORT' AND (
            (signing_key_id IS NULL AND ed25519_signature IS NULL AND event_kind = 'REGISTER')
            OR (signing_key_id IS NOT NULL AND ed25519_signature IS NOT NULL
                AND event_kind <> 'REGISTER')))
        OR (producer_kind = 'ADOPTED_EXTERNAL' AND event_kind <> 'ADOPT'
            AND signing_key_id IS NOT NULL
            AND ed25519_signature IS NOT NULL)
    ),
    CONSTRAINT managed_copy_events_first_register CHECK (
        (revision = 1 AND event_kind = 'REGISTER')
        OR (revision > 1 AND event_kind <> 'REGISTER')
    )
);

-- An owner VERIFY retains the exact independently signed physical-copy check. This is
-- evidence of one encrypted BASE/WAL object's usability, never pair readiness or freshness.
CREATE TABLE claimcore.managed_copy_verifications (
    verification_event_id uuid PRIMARY KEY,
    copy_id uuid NOT NULL,
    copy_revision bigint NOT NULL CHECK (copy_revision > 1),
    verifier_signing_key_id uuid NOT NULL
        REFERENCES claimcore.managed_copy_signers(signing_key_id),
    verifier_holder_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    nonce bytea NOT NULL CHECK (octet_length(nonce) = 32),
    canonical_report bytea NOT NULL CHECK (octet_length(canonical_report) BETWEEN 1 AND 16384),
    ed25519_signature bytea NOT NULL CHECK (octet_length(ed25519_signature) = 64),
    report_sha256 bytea NOT NULL CHECK (octet_length(report_sha256) = 32),
    archive_object_id uuid NOT NULL CHECK (
        archive_object_id <> '00000000-0000-0000-0000-000000000000'
    ),
    cluster_name text NOT NULL CHECK (cluster_name IN ('PRIMARY','WITNESS')),
    copy_kind text NOT NULL CHECK (copy_kind IN ('BASE','WAL')),
    location_commitment bytea NOT NULL CHECK (octet_length(location_commitment) = 32),
    ciphertext_sha256 bytea NOT NULL CHECK (octet_length(ciphertext_sha256) = 32),
    ciphertext_bytes bigint NOT NULL CHECK (ciphertext_bytes > 0),
    decrypted_sha256 bytea NOT NULL CHECK (octet_length(decrypted_sha256) = 32),
    decrypted_bytes bigint NOT NULL CHECK (decrypted_bytes > 0),
    postgres_system_id text NOT NULL CHECK (postgres_system_id ~ '^[0-9]{1,20}$'),
    timeline integer NOT NULL CHECK (timeline > 0),
    backup_manifest_sha256 bytea CHECK (
        backup_manifest_sha256 IS NULL OR octet_length(backup_manifest_sha256) = 32
    ),
    wal_segment text CHECK (wal_segment IS NULL OR wal_segment ~ '^[0-9A-F]{24}$'),
    recovered_row_count bigint CHECK (
        recovered_row_count IS NULL OR recovered_row_count >= 0
    ),
    wal_segment_bytes integer NOT NULL CHECK (
        wal_segment_bytes BETWEEN 1048576 AND 1073741824
        AND (wal_segment_bytes & (wal_segment_bytes - 1)) = 0
    ),
    checked_at timestamptz NOT NULL,
    valid_until timestamptz NOT NULL CHECK (
        valid_until > checked_at AND valid_until <= checked_at + interval '5 minutes'
    ),
    witness_cutoff_sequence bigint NOT NULL CHECK (witness_cutoff_sequence >= 0),
    witness_cutoff_hash bytea NOT NULL CHECK (octet_length(witness_cutoff_hash) = 32),
    UNIQUE (verifier_signing_key_id,nonce),
    UNIQUE (copy_id,copy_revision),
    CONSTRAINT managed_copy_verification_kind_shape CHECK (
        (copy_kind='BASE' AND backup_manifest_sha256 IS NOT NULL
            AND wal_segment IS NULL AND recovered_row_count IS NOT NULL)
        OR (copy_kind='WAL' AND backup_manifest_sha256 IS NULL
            AND wal_segment IS NOT NULL AND recovered_row_count IS NULL)
    ),
    FOREIGN KEY (verification_event_id,copy_id)
        REFERENCES claimcore.managed_copy_events(event_id,copy_id),
    FOREIGN KEY (copy_id,copy_revision)
        REFERENCES claimcore.managed_copy_events(copy_id,revision)
);

-- Owner-only adoption makes an already-known case copy location-addressable. It never
-- rewrites a PRODUCT_EXPORT receipt/REGISTER or treats custody as deletion. External origins
-- require an independently published pre-erasure registry checkpoint; a later assertion of
-- location alone cannot establish that an old copy existed before the privacy fence.
CREATE TABLE claimcore.managed_copy_adoption_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    adoption_event_id uuid NOT NULL CHECK (
        adoption_event_id <> '00000000-0000-0000-0000-000000000000'
    ),
    copy_id uuid NOT NULL CHECK (copy_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    origin_kind text NOT NULL CHECK (origin_kind IN ('PRODUCT_EXPORT','ADOPTED_EXTERNAL')),
    export_id uuid REFERENCES claimcore.recovery_artifact_exports(export_id),
    ciphertext_sha256 bytea NOT NULL CHECK (octet_length(ciphertext_sha256) = 32),
    ciphertext_bytes bigint NOT NULL CHECK (ciphertext_bytes > 0),
    pre_fence_kind text NOT NULL CHECK (pre_fence_kind IN (
        'PRODUCT_EXPORT_RECEIPT','PUBLISHED_REGISTRY'
    )),
    pre_fence_sequence bigint NOT NULL CHECK (pre_fence_sequence > 0),
    pre_fence_hash bytea NOT NULL CHECK (octet_length(pre_fence_hash) = 32),
    location_commitment bytea NOT NULL CHECK (octet_length(location_commitment) = 32),
    retain_until timestamptz NOT NULL,
    owner_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    owner_grant_revision bigint NOT NULL CHECK (owner_grant_revision > 0),
    approved_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > approved_at),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (
        octet_length(candidate_sha256) = 32 AND candidate_sha256 = sha256(canonical_action)
    ),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    UNIQUE (adoption_event_id,owner_actor_id),
    UNIQUE (approval_id,adoption_event_id,case_id,owner_actor_id,owner_grant_revision),
    UNIQUE (case_id,approval_id),
    UNIQUE (witness_epoch,witness_sequence),
    CHECK (
        (origin_kind = 'PRODUCT_EXPORT' AND export_id IS NOT NULL
            AND export_id = copy_id
            AND pre_fence_kind = 'PRODUCT_EXPORT_RECEIPT')
        OR (origin_kind = 'ADOPTED_EXTERNAL' AND export_id IS NULL
            AND pre_fence_kind = 'PUBLISHED_REGISTRY')
    )
);

CREATE TABLE claimcore.managed_copy_adoptions (
    adoption_event_id uuid PRIMARY KEY,
    copy_id uuid NOT NULL UNIQUE,
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    origin_kind text NOT NULL CHECK (origin_kind IN ('PRODUCT_EXPORT','ADOPTED_EXTERNAL')),
    export_id uuid REFERENCES claimcore.recovery_artifact_exports(export_id),
    copy_revision bigint NOT NULL CHECK (copy_revision > 0),
    ciphertext_sha256 bytea NOT NULL CHECK (octet_length(ciphertext_sha256) = 32),
    ciphertext_bytes bigint NOT NULL CHECK (ciphertext_bytes > 0),
    captured_at timestamptz NOT NULL,
    retain_until timestamptz NOT NULL CHECK (retain_until > captured_at),
    pre_fence_kind text NOT NULL CHECK (pre_fence_kind IN (
        'PRODUCT_EXPORT_RECEIPT','PUBLISHED_REGISTRY'
    )),
    pre_fence_sequence bigint NOT NULL CHECK (pre_fence_sequence > 0),
    pre_fence_hash bytea NOT NULL CHECK (octet_length(pre_fence_hash) = 32),
    location_commitment bytea NOT NULL CHECK (octet_length(location_commitment) = 32),
    custodian_commitment bytea NOT NULL CHECK (octet_length(custodian_commitment) = 32),
    custodian_signing_key_id uuid NOT NULL REFERENCES claimcore.managed_copy_signers(signing_key_id),
    registry_signing_key_id uuid NOT NULL REFERENCES claimcore.managed_copy_signers(signing_key_id),
    inspector_signing_key_id uuid NOT NULL REFERENCES claimcore.managed_copy_signers(signing_key_id),
    custodian_holder_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    registry_holder_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    inspector_holder_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    owner_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    owner_grant_revision bigint NOT NULL CHECK (owner_grant_revision > 0),
    owner_approval_id uuid NOT NULL,
    executor_kind text NOT NULL CHECK (executor_kind = 'SCHEMA_OWNER_PROCESS'),
    actor_authority_revision bigint NOT NULL CHECK (actor_authority_revision > 0),
    custodian_canonical bytea NOT NULL CHECK (octet_length(custodian_canonical) BETWEEN 1 AND 16384),
    custodian_signature bytea NOT NULL CHECK (octet_length(custodian_signature) = 64),
    registry_canonical bytea NOT NULL CHECK (octet_length(registry_canonical) BETWEEN 1 AND 16384),
    registry_signature bytea NOT NULL CHECK (octet_length(registry_signature) = 64),
    inspection_canonical bytea NOT NULL CHECK (octet_length(inspection_canonical) BETWEEN 1 AND 16384),
    inspection_signature bytea NOT NULL CHECK (octet_length(inspection_signature) = 64),
    inspection_report_sha256 bytea NOT NULL CHECK (octet_length(inspection_report_sha256) = 32),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 16384),
    candidate_sha256 bytea NOT NULL CHECK (
        octet_length(candidate_sha256) = 32 AND candidate_sha256 = sha256(canonical_action)
    ),
    previous_copy_hash bytea NOT NULL CHECK (octet_length(previous_copy_hash) = 32),
    copy_event_hash bytea NOT NULL CHECK (octet_length(copy_event_hash) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    adopted_at timestamptz NOT NULL CHECK (adopted_at >= captured_at),
    valid_until timestamptz NOT NULL CHECK (
        valid_until > adopted_at AND valid_until <= adopted_at + interval '24 hours'
    ),
    CHECK (pre_fence_sequence < witness_sequence),
    UNIQUE (witness_epoch,witness_sequence),
    UNIQUE (case_id,adoption_event_id),
    FOREIGN KEY (adoption_event_id,copy_id)
        REFERENCES claimcore.managed_copy_events(event_id,copy_id),
    FOREIGN KEY (copy_id,case_id)
        REFERENCES claimcore.managed_copies(copy_id,source_case_id),
    FOREIGN KEY (owner_approval_id,adoption_event_id,case_id,owner_actor_id,owner_grant_revision)
        REFERENCES claimcore.managed_copy_adoption_approvals
            (approval_id,adoption_event_id,case_id,owner_actor_id,owner_grant_revision),
    CONSTRAINT managed_copy_adoptions_origin CHECK (
        (origin_kind = 'PRODUCT_EXPORT' AND export_id IS NOT NULL
            AND export_id = copy_id AND copy_revision = 2
            AND pre_fence_kind = 'PRODUCT_EXPORT_RECEIPT')
        OR (origin_kind = 'ADOPTED_EXTERNAL' AND export_id IS NULL AND copy_revision = 1
            AND pre_fence_kind = 'PUBLISHED_REGISTRY')
    ),
    CONSTRAINT managed_copy_adoptions_distinct_custody CHECK (
        custodian_signing_key_id <> registry_signing_key_id
        AND custodian_signing_key_id <> inspector_signing_key_id
        AND registry_signing_key_id <> inspector_signing_key_id
        AND custodian_holder_actor_id <> inspector_holder_actor_id
        AND registry_holder_actor_id <> inspector_holder_actor_id
        AND owner_actor_id <> inspector_holder_actor_id
    )
);
CREATE INDEX managed_copy_adoptions_by_case
    ON claimcore.managed_copy_adoptions(case_id,copy_id);

CREATE TABLE claimcore.managed_copy_adoption_approval_uses (
    approval_id uuid PRIMARY KEY,
    adoption_event_id uuid NOT NULL UNIQUE,
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    used_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (case_id,approval_id)
        REFERENCES claimcore.managed_copy_adoption_approvals(case_id,approval_id),
    FOREIGN KEY (case_id,adoption_event_id)
        REFERENCES claimcore.managed_copy_adoptions(case_id,adoption_event_id)
);

-- A known unmanaged location can be adopted only if an independently signed, CASE-witnessed
-- registry publication existed before the erasure-request fence. This nonpayload receipt
-- retains pseudonymous identity and HMAC custody commitments, never a raw private path.
-- Its validity window gates the original publication, not later historical provenance.
CREATE TABLE claimcore.managed_copy_external_publications (
    publication_id uuid PRIMARY KEY CHECK (
        publication_id <> '00000000-0000-0000-0000-000000000000'
    ),
    copy_id uuid NOT NULL UNIQUE CHECK (copy_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL CHECK (case_id <> '00000000-0000-0000-0000-000000000000'),
    installation_id uuid NOT NULL CHECK (
        installation_id <> '00000000-0000-0000-0000-000000000000'
    ),
    lineage_id uuid NOT NULL CHECK (lineage_id <> '00000000-0000-0000-0000-000000000000'),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    encryption_key_id uuid NOT NULL CHECK (
        encryption_key_id <> '00000000-0000-0000-0000-000000000000'
    ),
    ciphertext_sha256 bytea NOT NULL CHECK (octet_length(ciphertext_sha256) = 32),
    ciphertext_bytes bigint NOT NULL CHECK (ciphertext_bytes > 0),
    captured_at timestamptz NOT NULL,
    retain_until timestamptz NOT NULL,
    location_commitment bytea NOT NULL CHECK (octet_length(location_commitment) = 32),
    custodian_commitment bytea NOT NULL CHECK (octet_length(custodian_commitment) = 32),
    registry_signing_key_id uuid NOT NULL REFERENCES claimcore.managed_copy_signers(signing_key_id),
    registry_holder_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    inspector_signing_key_id uuid NOT NULL REFERENCES claimcore.managed_copy_signers(signing_key_id),
    inspector_holder_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    registry_canonical bytea NOT NULL CHECK (octet_length(registry_canonical) BETWEEN 1 AND 16384),
    registry_signature bytea NOT NULL CHECK (octet_length(registry_signature) = 64),
    inspection_canonical bytea NOT NULL CHECK (octet_length(inspection_canonical) BETWEEN 1 AND 16384),
    inspection_signature bytea NOT NULL CHECK (octet_length(inspection_signature) = 64),
    actor_authority_revision bigint NOT NULL CHECK (actor_authority_revision > 0),
    case_revision bigint NOT NULL CHECK (case_revision > 0),
    executor_kind text NOT NULL CHECK (executor_kind = 'SCHEMA_OWNER_PROCESS'),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 16384),
    candidate_sha256 bytea NOT NULL CHECK (
        octet_length(candidate_sha256) = 32 AND candidate_sha256 = sha256(canonical_action)
    ),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    published_at timestamptz NOT NULL,
    valid_until timestamptz NOT NULL,
    UNIQUE (case_id,copy_id),
    UNIQUE (witness_epoch,witness_sequence),
    CHECK (registry_signing_key_id <> inspector_signing_key_id),
    CHECK (registry_holder_actor_id <> inspector_holder_actor_id),
    CHECK (captured_at <= published_at AND retain_until > published_at),
    CHECK (valid_until > published_at AND valid_until <= published_at + interval '10 minutes')
);
CREATE INDEX managed_copy_external_publications_by_case
    ON claimcore.managed_copy_external_publications(case_id,publication_id);

-- A separately authenticated verifier approves one exact absence report and copy revision.
-- Owner deletion consumes this witnessed approval; expiry or a missing file is not proof.
CREATE TABLE claimcore.managed_copy_deletion_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    deletion_event_id uuid NOT NULL CHECK (deletion_event_id <> '00000000-0000-0000-0000-000000000000'),
    copy_id uuid NOT NULL REFERENCES claimcore.managed_copies(copy_id),
    verifier_signing_key_id uuid NOT NULL REFERENCES claimcore.managed_copy_signers(signing_key_id),
    expected_copy_revision bigint NOT NULL CHECK (expected_copy_revision > 0),
    location_commitment bytea NOT NULL CHECK (octet_length(location_commitment) = 32),
    inspection_report_sha256 bytea NOT NULL CHECK (octet_length(inspection_report_sha256) = 32),
    witness_cutoff_sequence bigint NOT NULL CHECK (witness_cutoff_sequence >= 0),
    witness_cutoff_hash bytea NOT NULL CHECK (octet_length(witness_cutoff_hash) = 32),
    approver_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    approver_grant_revision bigint NOT NULL CHECK (approver_grant_revision > 0),
    expires_at timestamptz NOT NULL,
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (deletion_event_id, approver_actor_id),
    UNIQUE (witness_epoch, witness_sequence),
    CHECK (expires_at > recorded_at)
);
CREATE INDEX managed_copy_deletion_approvals_by_copy
    ON claimcore.managed_copy_deletion_approvals (copy_id, deletion_event_id);

CREATE TABLE claimcore.managed_copy_deletion_approval_uses (
    approval_id uuid PRIMARY KEY REFERENCES claimcore.managed_copy_deletion_approvals(approval_id),
    deletion_event_id uuid NOT NULL UNIQUE REFERENCES claimcore.managed_copy_events(event_id),
    used_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX managed_copy_events_by_copy ON claimcore.managed_copy_events (copy_id, revision);
CREATE INDEX managed_copies_by_state ON claimcore.managed_copies (state, retain_until, copy_id);
CREATE INDEX managed_copies_by_case ON claimcore.managed_copies (source_case_id, state, copy_id)
    WHERE source_case_id IS NOT NULL;

ALTER TABLE claimcore.case_changes
    ADD FOREIGN KEY (preparer_actor_id) REFERENCES claimcore.actors(actor_id),
    ADD FOREIGN KEY (importer_actor_id) REFERENCES claimcore.actors(actor_id),
    ADD FOREIGN KEY (submitter_actor_id) REFERENCES claimcore.actors(actor_id),
    ADD FOREIGN KEY (resolver_actor_id) REFERENCES claimcore.actors(actor_id),
    ADD FOREIGN KEY (accepted_actor_id) REFERENCES claimcore.actors(actor_id);


CREATE TABLE claimcore.request_preparations (
    operation_id uuid PRIMARY KEY
        CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    witness_event_id uuid NOT NULL UNIQUE
        CHECK (witness_event_id <> '00000000-0000-0000-0000-000000000000'),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    witness_candidate_sha256 bytea NOT NULL CHECK (octet_length(witness_candidate_sha256) = 32),
    UNIQUE (witness_epoch, witness_sequence),
    case_id uuid NOT NULL CHECK (case_id <> '00000000-0000-0000-0000-000000000000'),
    preparer_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    importer_actor_id uuid REFERENCES claimcore.actors(actor_id),
    preparer_grant_revision bigint NOT NULL CHECK (preparer_grant_revision > 0),
    canonical_request_format smallint NOT NULL CHECK (canonical_request_format = 3),
    request_sha256 text NOT NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    canonical_request bytea NOT NULL
        CHECK (octet_length(canonical_request) BETWEEN 1 AND 65536),
    prepared_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    prepared_application_version text NOT NULL CHECK (
        char_length(prepared_application_version) BETWEEN 1 AND 200
        AND btrim(prepared_application_version) = prepared_application_version
    ),
    preparing_contract_fingerprint text NOT NULL CHECK (preparing_contract_fingerprint ~ '^[0-9a-f]{64}$'),
    preparing_contract_kind text NOT NULL CHECK (preparing_contract_kind = 'SEMANTIC_CORE_V1')
);

-- The first submission marker is append-only; revocation is separate durable authority.
CREATE TABLE claimcore.request_preparation_lifecycle (
    operation_id uuid PRIMARY KEY REFERENCES claimcore.request_preparations(operation_id) ON DELETE CASCADE,
    state text NOT NULL CHECK (state = 'SUBMISSION_STARTED'),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX request_preparation_lifecycle_retention
    ON claimcore.request_preparation_lifecycle (state, recorded_at);

-- Schema-owner pruning is recorded without retaining request bytes or claim payloads in the audit row.
CREATE TABLE claimcore.request_preparation_prunes (
    prune_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    executed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    executed_by text NOT NULL DEFAULT session_user,
    dry_run boolean NOT NULL,
    settled_retention_days integer NOT NULL CHECK (settled_retention_days BETWEEN 1 AND 3650),
    abandoned_retention_days integer NOT NULL CHECK (abandoned_retention_days BETWEEN 1 AND 3650),
    batch_limit integer NOT NULL CHECK (batch_limit BETWEEN 1 AND 1000),
    candidate_count integer NOT NULL CHECK (candidate_count >= 0),
    deleted_count integer NOT NULL CHECK (deleted_count BETWEEN 0 AND candidate_count),
    CHECK (NOT dry_run OR deleted_count = 0)
);

CREATE TABLE claimcore.request_submission_attempts (
    attempt_id uuid PRIMARY KEY
        CHECK (attempt_id <> '00000000-0000-0000-0000-000000000000'),
    attempt_ordinal bigint NOT NULL CHECK (attempt_ordinal > 0),
    witness_event_id uuid NOT NULL UNIQUE CHECK (witness_event_id = attempt_id),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    witness_candidate_sha256 bytea NOT NULL CHECK (octet_length(witness_candidate_sha256) = 32),
    UNIQUE (witness_epoch, witness_sequence),
    operation_id uuid NOT NULL
        REFERENCES claimcore.request_preparations(operation_id) ON DELETE CASCADE,
    UNIQUE (operation_id, attempt_ordinal),
    submitter_actor_id uuid REFERENCES claimcore.actors(actor_id),
    resolver_actor_id uuid REFERENCES claimcore.actors(actor_id),
    grant_revision bigint NOT NULL CHECK (grant_revision > 0),
    CONSTRAINT request_submission_attempts_actor_phase CHECK (
        (submitter_actor_id IS NOT NULL) <> (resolver_actor_id IS NOT NULL)
    ),
    started_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX request_submission_attempts_by_operation
    ON claimcore.request_submission_attempts (operation_id, started_at, attempt_id);

CREATE TABLE claimcore.request_submission_settlements (
    attempt_id uuid PRIMARY KEY
        REFERENCES claimcore.request_submission_attempts(attempt_id) ON DELETE CASCADE,
    outcome text NOT NULL CHECK (outcome IN ('ACCEPTED', 'REJECTED', 'ERROR', 'REVOKED_BEFORE_EXECUTION')),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE claimcore.operation_revocations (
    operation_id uuid PRIMARY KEY
        CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    witness_event_id uuid NOT NULL UNIQUE
        CHECK (witness_event_id <> '00000000-0000-0000-0000-000000000000'
            AND witness_event_id <> operation_id),
    case_id uuid NOT NULL CHECK (case_id <> '00000000-0000-0000-0000-000000000000'),
    canonical_request_format smallint NOT NULL CHECK (canonical_request_format = 3),
    request_sha256 text NOT NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    revoking_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    grant_revision bigint NOT NULL CHECK (grant_revision > 0),
    revoked_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    UNIQUE (witness_epoch, witness_sequence),
    reason text NOT NULL CHECK (reason = 'OPERATOR_DISMISSAL')
);

CREATE INDEX operation_revocations_by_revoked_at
    ON claimcore.operation_revocations (revoked_at, operation_id);

CREATE INDEX request_preparations_by_prepared_at
    ON claimcore.request_preparations (prepared_at, operation_id);

REVOKE ALL ON ALL TABLES IN SCHEMA claimcore FROM PUBLIC, claimcore_app;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA claimcore FROM PUBLIC, claimcore_app;
GRANT USAGE ON SCHEMA claimcore TO claimcore_app;
GRANT SELECT ON claimcore.schema_baseline, claimcore.installation_lineage,
    claimcore.writer_handoffs, claimcore.writer_handoff_preparations,
    claimcore.writer_activations,
    claimcore.writer_handoff_approval_uses,
    claimcore.writer_handoff_abort_approvals,
    claimcore.writer_handoff_aborts,
    claimcore.writer_handoff_abort_approval_uses TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.writer_handoff_approvals TO claimcore_app;
GRANT SELECT, UPDATE ON claimcore.authority_tip TO claimcore_app;
GRANT SELECT, INSERT, UPDATE ON claimcore.actors, claimcore.actor_grants TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.actor_authority_events TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.installation_data_use_approvals TO claimcore_app;
GRANT SELECT ON claimcore.installation_data_use_plans,
    claimcore.installation_data_use_activations TO claimcore_app;
GRANT SELECT ON claimcore.installation_data_use_approval_uses TO claimcore_app;
GRANT SELECT, INSERT, UPDATE ON claimcore.cases TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_lifecycle_events,
    claimcore.case_lifecycle_approvals TO claimcore_app;
GRANT SELECT, INSERT, UPDATE ON claimcore.case_holds TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_erasure_tombstones,
    claimcore.case_erasure_operation_denials TO claimcore_app;
GRANT SELECT, UPDATE ON claimcore.case_erasure_authority_tip TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_erasure_holds,
    claimcore.case_erasure_hold_releases TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_erasure_prune_approvals TO claimcore_app;
GRANT SELECT ON claimcore.case_erasure_prune_targets TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_erasure_terminal_approvals TO claimcore_app;
GRANT SELECT ON claimcore.case_erasure_terminal_events,
    claimcore.case_erasure_terminal_approval_uses TO claimcore_app;
GRANT SELECT ON claimcore.case_erasure_purge_approvals TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.recovery_artifact_exports TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.recovery_artifact_payloads TO claimcore_app;
GRANT SELECT ON claimcore.managed_copy_signers, claimcore.managed_copy_signer_events,
    claimcore.managed_copy_signer_approval_uses TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.managed_copy_signer_approvals TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.managed_copies TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.managed_copy_events TO claimcore_app;
GRANT SELECT ON claimcore.managed_copy_adoptions TO claimcore_app;
GRANT SELECT ON claimcore.managed_copy_external_publications TO claimcore_app;
GRANT SELECT ON claimcore.managed_copy_verifications TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.managed_copy_adoption_approvals TO claimcore_app;
GRANT SELECT ON claimcore.managed_copy_adoption_approval_uses TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.managed_copy_deletion_approvals TO claimcore_app;
GRANT SELECT ON claimcore.managed_copy_deletion_approval_uses TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_changes, claimcore.request_preparations,
    claimcore.request_preparation_lifecycle, claimcore.request_submission_attempts,
    claimcore.request_submission_settlements, claimcore.operation_revocations TO claimcore_app;
