
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
