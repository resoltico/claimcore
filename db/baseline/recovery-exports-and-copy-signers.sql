
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
