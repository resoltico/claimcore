
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
