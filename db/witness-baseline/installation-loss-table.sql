CREATE TABLE claimcore_witness.installation_loss_retirements (
    retirement_id uuid PRIMARY KEY CHECK (retirement_id <> '00000000-0000-0000-0000-000000000000'),
    installation_id uuid NOT NULL,
    lineage_id uuid NOT NULL,
    old_epoch bigint NOT NULL CHECK (old_epoch > 0),
    previous_sequence bigint NOT NULL CHECK (previous_sequence >= 0),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    canonical_decision bytea NOT NULL CHECK (octet_length(canonical_decision) BETWEEN 1 AND 16384),
    canonical_sha256 bytea NOT NULL CHECK (octet_length(canonical_sha256) = 32),
    signature_one bytea NOT NULL CHECK (octet_length(signature_one) = 64),
    signature_two bytea NOT NULL CHECK (octet_length(signature_two) = 64),
    signer_one_id uuid NOT NULL,
    signer_two_id uuid NOT NULL,
    owner_one_actor_id uuid NOT NULL,
    owner_two_actor_id uuid NOT NULL,
    operation_set_kind text NOT NULL CHECK (operation_set_kind IN ('KNOWN_OPERATIONS','UNKNOWN_OPERATIONS')),
    known_operation_count integer NOT NULL CHECK (known_operation_count BETWEEN 0 AND 10000),
    known_operation_digest bytea NOT NULL CHECK (octet_length(known_operation_digest) = 32),
    intent_sequence bigint NOT NULL UNIQUE CHECK (intent_sequence = previous_sequence + 1),
    intent_hash bytea NOT NULL CHECK (octet_length(intent_hash) = 32),
    settlement_sequence bigint UNIQUE CHECK (settlement_sequence IS NULL OR settlement_sequence = intent_sequence + 1),
    settlement_hash bytea CHECK (settlement_hash IS NULL OR octet_length(settlement_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT loss_retirement_distinct_signers CHECK (
        signer_one_id <> signer_two_id AND owner_one_actor_id <> owner_two_actor_id
    ),
    CONSTRAINT loss_retirement_settlement_shape CHECK (
        (settlement_sequence IS NULL AND settlement_hash IS NULL)
        OR (settlement_sequence IS NOT NULL AND settlement_hash IS NOT NULL)
    ),
    CONSTRAINT loss_retirement_unknown_set CHECK (
        operation_set_kind <> 'UNKNOWN_OPERATIONS' OR known_operation_count = 0
    )
);

-- A pending incident ticket closes every existing owner and runtime writer lane. The
-- sole allowed later journal insert is that ticket's exact terminal settlement.
