CREATE TABLE claimcore_witness.writer_handoffs (
    handoff_id uuid PRIMARY KEY,
    old_generation bigint NOT NULL CHECK (old_generation > 0),
    new_generation bigint NOT NULL CHECK (new_generation = old_generation + 1),
    previous_sequence bigint NOT NULL CHECK (previous_sequence >= 0),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    new_capability_sha256 bytea NOT NULL CHECK (octet_length(new_capability_sha256) = 32),
    checkpoint_signing_key_id uuid NOT NULL,
    prepare_canonical bytea NOT NULL CHECK (octet_length(prepare_canonical) BETWEEN 1 AND 16384),
    prepare_signature bytea NOT NULL CHECK (octet_length(prepare_signature) = 64),
    approval_one_id uuid NOT NULL,
    approval_two_id uuid NOT NULL,
    CONSTRAINT writer_handoff_distinct_approvals CHECK (approval_one_id <> approval_two_id),
    prepare_candidate_sha256 bytea NOT NULL CHECK (octet_length(prepare_candidate_sha256) = 32),
    prepare_sequence bigint NOT NULL UNIQUE CHECK (prepare_sequence > 0),
    prepare_hash bytea NOT NULL CHECK (octet_length(prepare_hash) = 32),
    settlement_candidate_sha256 bytea CHECK (
        settlement_candidate_sha256 IS NULL OR octet_length(settlement_candidate_sha256) = 32
    ),
    settlement_sequence bigint UNIQUE CHECK (settlement_sequence IS NULL OR settlement_sequence > 0),
    settlement_hash bytea CHECK (settlement_hash IS NULL OR octet_length(settlement_hash) = 32),
    settlement_canonical bytea CHECK (
        settlement_canonical IS NULL OR octet_length(settlement_canonical) BETWEEN 1 AND 16384
    ),
    settlement_signature bytea CHECK (
        settlement_signature IS NULL OR octet_length(settlement_signature) = 64
    ),
    activation_id uuid UNIQUE,
    activation_candidate_sha256 bytea CHECK (
        activation_candidate_sha256 IS NULL OR octet_length(activation_candidate_sha256) = 32
    ),
    activation_intent_sequence bigint UNIQUE CHECK (
        activation_intent_sequence IS NULL OR activation_intent_sequence > 0
    ),
    activation_intent_hash bytea CHECK (
        activation_intent_hash IS NULL OR octet_length(activation_intent_hash) = 32
    ),
    activation_sequence bigint UNIQUE CHECK (activation_sequence IS NULL OR activation_sequence > 0),
    activation_hash bytea CHECK (activation_hash IS NULL OR octet_length(activation_hash) = 32),
    activation_canonical bytea CHECK (
        activation_canonical IS NULL OR octet_length(activation_canonical) BETWEEN 1 AND 16384
    ),
    abort_candidate_sha256 bytea CHECK (
        abort_candidate_sha256 IS NULL OR octet_length(abort_candidate_sha256) = 32
    ),
    abort_sequence bigint UNIQUE CHECK (abort_sequence IS NULL OR abort_sequence > prepare_sequence),
    abort_hash bytea CHECK (abort_hash IS NULL OR octet_length(abort_hash) = 32),
    abort_canonical bytea CHECK (
        abort_canonical IS NULL OR octet_length(abort_canonical) BETWEEN 1 AND 16384
    ),
    abort_signature_one bytea CHECK (
        abort_signature_one IS NULL OR octet_length(abort_signature_one) = 64
    ),
    abort_signature_two bytea CHECK (
        abort_signature_two IS NULL OR octet_length(abort_signature_two) = 64
    ),
    abort_signing_key_one uuid,
    abort_signing_key_two uuid,
    CONSTRAINT writer_handoff_abort_distinct_keys CHECK (
        abort_signing_key_one IS NULL OR abort_signing_key_two IS NULL
        OR abort_signing_key_one <> abort_signing_key_two
    ),
    CONSTRAINT writer_handoff_settlement_shape CHECK (
        (settlement_candidate_sha256 IS NULL AND settlement_sequence IS NULL
            AND settlement_hash IS NULL AND settlement_canonical IS NULL
            AND settlement_signature IS NULL)
        OR (settlement_candidate_sha256 IS NOT NULL AND settlement_sequence IS NOT NULL
            AND settlement_hash IS NOT NULL AND settlement_canonical IS NOT NULL
            AND settlement_signature IS NOT NULL)
    ),
    CONSTRAINT writer_handoff_activation_shape CHECK (
        (activation_id IS NULL AND activation_candidate_sha256 IS NULL
            AND activation_intent_sequence IS NULL AND activation_intent_hash IS NULL
            AND activation_sequence IS NULL AND activation_hash IS NULL
            AND activation_canonical IS NULL)
        OR (activation_id IS NOT NULL AND activation_candidate_sha256 IS NOT NULL
            AND activation_intent_sequence IS NOT NULL AND activation_intent_hash IS NOT NULL
            AND activation_sequence = activation_intent_sequence + 1
            AND activation_hash IS NOT NULL
            AND activation_canonical IS NOT NULL AND settlement_sequence IS NOT NULL)
    ),
    CONSTRAINT writer_handoff_abort_shape CHECK (
        (abort_candidate_sha256 IS NULL AND abort_sequence IS NULL AND abort_hash IS NULL
            AND abort_canonical IS NULL AND abort_signature_one IS NULL
            AND abort_signature_two IS NULL AND abort_signing_key_one IS NULL
            AND abort_signing_key_two IS NULL)
        OR (abort_candidate_sha256 IS NOT NULL AND abort_sequence IS NOT NULL
            AND abort_hash IS NOT NULL AND abort_canonical IS NOT NULL
            AND abort_signature_one IS NOT NULL AND abort_signature_two IS NOT NULL
            AND abort_signing_key_one IS NOT NULL AND abort_signing_key_two IS NOT NULL
            AND settlement_sequence IS NULL)
    )
);

-- This owner-only row and its two journal tickets are retained even when primary recovery
-- evidence is incomplete. A pending ticket already fences all ordinary writer authority.
