
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
