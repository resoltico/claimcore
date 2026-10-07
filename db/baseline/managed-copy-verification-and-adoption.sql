
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
