-- Fresh, independent PostgreSQL witness installation.
-- The schema owner executes this script in one transaction after creating
-- claimcore_witness_writer and claimcore_witness_auditor as distinct NOINHERIT,
-- non-owner login roles. The auditor has no append or owner-function authority.
CREATE SCHEMA claimcore_witness;
REVOKE ALL ON SCHEMA claimcore_witness FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE TABLE claimcore_witness.installation (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    installation_id uuid NOT NULL,
    lineage_id uuid NOT NULL,
    initial_key_id uuid NOT NULL,
    active_key_id uuid NOT NULL,
    key_check_envelope bytea NOT NULL CHECK (octet_length(key_check_envelope) BETWEEN 32 AND 4096),
    epoch bigint NOT NULL CHECK (epoch > 0),
    writer_generation bigint NOT NULL DEFAULT 1 CHECK (writer_generation > 0),
    loss_retirement_pending boolean NOT NULL DEFAULT false,
    loss_retired boolean NOT NULL DEFAULT false,
    loss_retirement_id uuid,
    loss_retirement_intent_sequence bigint CHECK (
        loss_retirement_intent_sequence IS NULL OR loss_retirement_intent_sequence > 0
    ),
    loss_retirement_intent_hash bytea CHECK (
        loss_retirement_intent_hash IS NULL OR octet_length(loss_retirement_intent_hash) = 32
    ),
    loss_retirement_sequence bigint CHECK (
        loss_retirement_sequence IS NULL OR loss_retirement_sequence > 0
    ),
    loss_retirement_hash bytea CHECK (
        loss_retirement_hash IS NULL OR octet_length(loss_retirement_hash) = 32
    ),
    CONSTRAINT installation_loss_retirement_shape CHECK (
        (NOT loss_retirement_pending AND NOT loss_retired AND loss_retirement_id IS NULL
            AND loss_retirement_intent_sequence IS NULL AND loss_retirement_intent_hash IS NULL
            AND loss_retirement_sequence IS NULL AND loss_retirement_hash IS NULL)
        OR (loss_retirement_pending AND NOT loss_retired AND loss_retirement_id IS NOT NULL
            AND loss_retirement_intent_sequence IS NOT NULL AND loss_retirement_intent_hash IS NOT NULL
            AND loss_retirement_sequence IS NULL AND loss_retirement_hash IS NULL)
        OR (NOT loss_retirement_pending AND loss_retired AND loss_retirement_id IS NOT NULL
            AND loss_retirement_intent_sequence IS NOT NULL AND loss_retirement_intent_hash IS NOT NULL
            AND loss_retirement_sequence = loss_retirement_intent_sequence + 1
            AND loss_retirement_hash IS NOT NULL)
    ),
    data_use_scope text NOT NULL CHECK (data_use_scope IN ('SYNTHETIC_ONLY','REAL_DATA')),
    data_use_phase text NOT NULL CHECK (data_use_phase IN ('BOOTSTRAP_NO_CASES','ACTIVE')),
    data_use_activation_event_id uuid,
    data_use_activation_intent_sequence bigint CHECK (
        data_use_activation_intent_sequence IS NULL OR data_use_activation_intent_sequence > 0
    ),
    data_use_activation_intent_hash bytea CHECK (
        data_use_activation_intent_hash IS NULL OR octet_length(data_use_activation_intent_hash) = 32
    ),
    data_use_activation_sequence bigint CHECK (
        data_use_activation_sequence IS NULL OR data_use_activation_sequence > 0
    ),
    data_use_activation_hash bytea CHECK (
        data_use_activation_hash IS NULL OR octet_length(data_use_activation_hash) = 32
    ),
    data_use_activation_canonical bytea CHECK (
        data_use_activation_canonical IS NULL OR octet_length(data_use_activation_canonical) BETWEEN 1 AND 8192
    ),
    writer_capability_sha256 bytea NOT NULL CHECK (octet_length(writer_capability_sha256) = 32),
    handoff_pending boolean NOT NULL DEFAULT false,
    activation_pending boolean NOT NULL DEFAULT false,
    activation_event_id uuid,
    activation_sequence bigint CHECK (activation_sequence IS NULL OR activation_sequence > 0),
    activation_hash bytea CHECK (activation_hash IS NULL OR octet_length(activation_hash) = 32),
    last_aborted_handoff_id uuid,
    last_aborted_handoff_sequence bigint CHECK (
        last_aborted_handoff_sequence IS NULL OR last_aborted_handoff_sequence > 0
    ),
    last_aborted_handoff_hash bytea CHECK (
        last_aborted_handoff_hash IS NULL OR octet_length(last_aborted_handoff_hash) = 32
    ),
    tip_sequence bigint NOT NULL DEFAULT 0 CHECK (tip_sequence >= 0),
    tip_hash bytea NOT NULL CHECK (octet_length(tip_hash) = 32),
    baseline_id text NOT NULL CHECK (baseline_id = 'claimcore-witness-v1'),
    baseline_sha256 text NOT NULL CHECK (baseline_sha256 ~ '^[0-9a-f]{64}$'),
    UNIQUE (installation_id, lineage_id),
    CONSTRAINT installation_data_use_shape CHECK (
        (data_use_scope='SYNTHETIC_ONLY' AND data_use_phase='ACTIVE'
            AND data_use_activation_event_id IS NULL AND data_use_activation_intent_sequence IS NULL
            AND data_use_activation_intent_hash IS NULL AND data_use_activation_sequence IS NULL
            AND data_use_activation_hash IS NULL AND data_use_activation_canonical IS NULL)
        OR (data_use_scope='REAL_DATA' AND
            ((data_use_phase='BOOTSTRAP_NO_CASES'
                AND data_use_activation_event_id IS NULL AND data_use_activation_intent_sequence IS NULL
                AND data_use_activation_intent_hash IS NULL AND data_use_activation_sequence IS NULL
                AND data_use_activation_hash IS NULL AND data_use_activation_canonical IS NULL)
             OR (data_use_phase='ACTIVE' AND data_use_activation_event_id IS NOT NULL
                AND data_use_activation_intent_sequence IS NOT NULL
                AND data_use_activation_intent_hash IS NOT NULL
                AND data_use_activation_sequence = data_use_activation_intent_sequence + 1
                AND data_use_activation_hash IS NOT NULL AND data_use_activation_canonical IS NOT NULL)))
    ),
    CONSTRAINT installation_abort_ticket_shape CHECK (
        (last_aborted_handoff_id IS NULL AND last_aborted_handoff_sequence IS NULL
            AND last_aborted_handoff_hash IS NULL)
        OR (last_aborted_handoff_id IS NOT NULL AND last_aborted_handoff_sequence IS NOT NULL
            AND last_aborted_handoff_hash IS NOT NULL)
    ),
    CONSTRAINT installation_activation_shape CHECK (
        (writer_generation = 1 AND NOT activation_pending AND activation_event_id IS NULL
            AND activation_sequence IS NULL AND activation_hash IS NULL)
        OR (writer_generation > 1 AND
            ((activation_pending AND activation_event_id IS NULL
                AND activation_sequence IS NULL AND activation_hash IS NULL)
             OR (NOT activation_pending AND activation_event_id IS NOT NULL
                AND activation_sequence IS NOT NULL AND activation_hash IS NOT NULL)))
    )
);
