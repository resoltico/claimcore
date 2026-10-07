
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
