
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
