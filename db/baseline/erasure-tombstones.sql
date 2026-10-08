
-- An erasure request reserves the reference before live case payload is purged.
-- These rows carry keyed, installation-bound commitments, not a raw case reference.
CREATE TABLE claimcore.case_erasure_tombstones (
    case_id uuid PRIMARY KEY CHECK (case_id <> '00000000-0000-0000-0000-000000000000'),
    suppression_key_id uuid NOT NULL CHECK (suppression_key_id <> '00000000-0000-0000-0000-000000000000'),
    reference_commitment bytea NOT NULL CHECK (octet_length(reference_commitment) = 32),
    phase text NOT NULL CHECK (phase IN (
        'ERASURE_REQUESTED', 'ERASURE_PENDING',
        'PAYLOAD_ERASED_SUPPRESSION_RETAINED', 'ERASURE_FINAL'
    )),
    source_revision bigint NOT NULL CHECK (source_revision > 0),
    source_disposition text NOT NULL CHECK (source_disposition IN ('ACTIVE', 'VOIDED_DATA_ENTRY_ERROR')),
    lifecycle_sequence bigint NOT NULL CHECK (lifecycle_sequence > 0),
    lifecycle_hash bytea NOT NULL CHECK (octet_length(lifecycle_hash) = 32),
    denial_count bigint NOT NULL CHECK (denial_count >= 0),
    denial_set_sha256 bytea NOT NULL CHECK (octet_length(denial_set_sha256) = 32),
    request_event_id uuid NOT NULL UNIQUE CHECK (request_event_id <> '00000000-0000-0000-0000-000000000000'),
    request_candidate_sha256 bytea CHECK (
        request_candidate_sha256 IS NULL OR octet_length(request_candidate_sha256) = 32
    ),
    request_candidate_commitment bytea CHECK (
        request_candidate_commitment IS NULL OR octet_length(request_candidate_commitment) = 32
    ),
    request_witness_sequence bigint NOT NULL CHECK (request_witness_sequence > 0),
    request_witness_epoch bigint NOT NULL CHECK (request_witness_epoch > 0),
    request_witness_entry_hash bytea NOT NULL CHECK (octet_length(request_witness_entry_hash) = 32),
    purge_event_id uuid UNIQUE,
    purge_executor_kind text CHECK (
        purge_executor_kind IS NULL OR purge_executor_kind = 'SCHEMA_OWNER_PROCESS'
    ),
    purge_proposal_commitment bytea CHECK (
        purge_proposal_commitment IS NULL OR octet_length(purge_proposal_commitment) = 32
    ),
    purge_valid_until timestamptz,
    purge_source_revision bigint CHECK (purge_source_revision IS NULL OR purge_source_revision > 0),
    purge_lifecycle_sequence bigint CHECK (
        purge_lifecycle_sequence IS NULL OR purge_lifecycle_sequence > 0
    ),
    purge_lifecycle_hash bytea CHECK (
        purge_lifecycle_hash IS NULL OR octet_length(purge_lifecycle_hash) = 32
    ),
    purge_witness_cutoff_sequence bigint CHECK (
        purge_witness_cutoff_sequence IS NULL OR purge_witness_cutoff_sequence >= 0
    ),
    purge_witness_cutoff_hash bytea CHECK (
        purge_witness_cutoff_hash IS NULL OR octet_length(purge_witness_cutoff_hash) = 32
    ),
    purge_subject_intent_count bigint CHECK (
        purge_subject_intent_count IS NULL OR purge_subject_intent_count >= 0
    ),
    purge_subject_intent_sha256 bytea CHECK (
        purge_subject_intent_sha256 IS NULL OR octet_length(purge_subject_intent_sha256) = 32
    ),
    purge_denial_count bigint CHECK (purge_denial_count IS NULL OR purge_denial_count >= 0),
    purge_denial_set_sha256 bytea CHECK (
        purge_denial_set_sha256 IS NULL OR octet_length(purge_denial_set_sha256) = 32
    ),
    purge_copy_inventory_sha256 bytea CHECK (
        purge_copy_inventory_sha256 IS NULL OR octet_length(purge_copy_inventory_sha256) = 32
    ),
    purge_canonical_action bytea CHECK (
        purge_canonical_action IS NULL OR octet_length(purge_canonical_action) BETWEEN 1 AND 16384
    ),
    purge_candidate_sha256 bytea CHECK (purge_candidate_sha256 IS NULL OR octet_length(purge_candidate_sha256) = 32),
    purge_witness_sequence bigint CHECK (purge_witness_sequence IS NULL OR purge_witness_sequence > 0),
    purge_witness_epoch bigint CHECK (purge_witness_epoch IS NULL OR purge_witness_epoch > 0),
    purge_witness_entry_hash bytea CHECK (purge_witness_entry_hash IS NULL OR octet_length(purge_witness_entry_hash) = 32),
    witness_prune_event_id uuid UNIQUE,
    witness_prune_candidate_sha256 bytea CHECK (
        witness_prune_candidate_sha256 IS NULL OR octet_length(witness_prune_candidate_sha256) = 32
    ),
    witness_prune_canonical_action bytea CHECK (
        witness_prune_canonical_action IS NULL OR
        octet_length(witness_prune_canonical_action) BETWEEN 1 AND 16384
    ),
    witness_prune_intent_sequence bigint CHECK (
        witness_prune_intent_sequence IS NULL OR witness_prune_intent_sequence > 0
    ),
    witness_prune_intent_epoch bigint CHECK (
        witness_prune_intent_epoch IS NULL OR witness_prune_intent_epoch > 0
    ),
    witness_prune_intent_hash bytea CHECK (
        witness_prune_intent_hash IS NULL OR octet_length(witness_prune_intent_hash) = 32
    ),
    witness_prune_cutoff_sequence bigint CHECK (
        witness_prune_cutoff_sequence IS NULL OR witness_prune_cutoff_sequence > 0
    ),
    witness_prune_cutoff_hash bytea CHECK (
        witness_prune_cutoff_hash IS NULL OR octet_length(witness_prune_cutoff_hash) = 32
    ),
    witness_prune_target_count bigint CHECK (
        witness_prune_target_count IS NULL OR witness_prune_target_count > 0
    ),
    witness_prune_target_digest bytea CHECK (
        witness_prune_target_digest IS NULL OR octet_length(witness_prune_target_digest) = 32
    ),
    witness_prune_copy_inventory_sha256 bytea CHECK (
        witness_prune_copy_inventory_sha256 IS NULL OR
        octet_length(witness_prune_copy_inventory_sha256) = 32
    ),
    witness_prune_authority_revision bigint CHECK (
        witness_prune_authority_revision IS NULL OR witness_prune_authority_revision >= 0
    ),
    witness_prune_authority_hash bytea CHECK (
        witness_prune_authority_hash IS NULL OR octet_length(witness_prune_authority_hash) = 32
    ),
    witness_prune_valid_until timestamptz,
    copy_absence_event_id uuid UNIQUE,
    suppression_final_event_id uuid UNIQUE,
    live_purged_at timestamptz,
    suppression_until timestamptz,
    retention_policy_id text CHECK (retention_policy_id IS NULL OR (
        char_length(retention_policy_id) BETWEEN 1 AND 128
        AND retention_policy_id = btrim(retention_policy_id)
        AND retention_policy_id !~ '[[:cntrl:]]'
    )),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    identity_coverage text NOT NULL DEFAULT 'PRIMARY_ONLY'
        CHECK (identity_coverage IN ('PRIMARY_ONLY', 'WITNESS_COMPLETE')),
    UNIQUE (suppression_key_id, reference_commitment),
    UNIQUE (case_id, purge_event_id),
    UNIQUE (case_id, witness_prune_event_id),
    UNIQUE (request_witness_epoch, request_witness_sequence),
    CONSTRAINT case_erasure_purge_evidence_group CHECK (
        (purge_event_id IS NULL AND purge_executor_kind IS NULL
         AND purge_proposal_commitment IS NULL
         AND purge_valid_until IS NULL AND purge_source_revision IS NULL
         AND purge_lifecycle_sequence IS NULL AND purge_lifecycle_hash IS NULL
         AND purge_witness_cutoff_sequence IS NULL AND purge_witness_cutoff_hash IS NULL
         AND purge_subject_intent_count IS NULL AND purge_subject_intent_sha256 IS NULL
         AND purge_denial_count IS NULL AND purge_denial_set_sha256 IS NULL
         AND purge_copy_inventory_sha256 IS NULL AND purge_canonical_action IS NULL
         AND purge_candidate_sha256 IS NULL
         AND purge_witness_sequence IS NULL AND purge_witness_epoch IS NULL
         AND purge_witness_entry_hash IS NULL AND live_purged_at IS NULL
         AND identity_coverage = 'PRIMARY_ONLY')
        OR (purge_event_id IS NOT NULL
            AND purge_executor_kind = 'SCHEMA_OWNER_PROCESS'
            AND purge_proposal_commitment IS NOT NULL
            AND purge_valid_until IS NOT NULL AND purge_source_revision IS NOT NULL
            AND purge_lifecycle_sequence IS NOT NULL AND purge_lifecycle_hash IS NOT NULL
            AND purge_witness_cutoff_sequence IS NOT NULL AND purge_witness_cutoff_hash IS NOT NULL
            AND purge_subject_intent_count IS NOT NULL AND purge_subject_intent_sha256 IS NOT NULL
            AND purge_denial_count IS NOT NULL AND purge_denial_set_sha256 IS NOT NULL
            AND purge_copy_inventory_sha256 IS NOT NULL AND purge_canonical_action IS NOT NULL
            AND purge_candidate_sha256 IS NOT NULL
            AND purge_witness_sequence IS NOT NULL AND purge_witness_epoch IS NOT NULL
            AND purge_witness_entry_hash IS NOT NULL AND live_purged_at IS NOT NULL
            AND identity_coverage = 'WITNESS_COMPLETE')
    ),
    CONSTRAINT case_erasure_phase_evidence CHECK (
        phase = 'ERASURE_REQUESTED' OR purge_event_id IS NOT NULL
    ),
    CONSTRAINT case_erasure_request_candidate_protection CHECK (
        (purge_event_id IS NULL AND request_candidate_sha256 IS NOT NULL
         AND request_candidate_commitment IS NULL)
        OR (purge_event_id IS NOT NULL AND request_candidate_sha256 IS NULL
            AND request_candidate_commitment IS NOT NULL)
    ),
    CONSTRAINT case_erasure_purge_order CHECK (
        purge_event_id IS NULL OR (
            purge_valid_until > live_purged_at
            AND purge_source_revision >= source_revision
            AND purge_lifecycle_sequence >= lifecycle_sequence
            AND purge_witness_sequence > purge_witness_cutoff_sequence
            AND purge_candidate_sha256 = sha256(purge_canonical_action)
        )
    ),
    CONSTRAINT case_erasure_witness_prune_group CHECK (
        (witness_prune_event_id IS NULL AND witness_prune_candidate_sha256 IS NULL
         AND witness_prune_canonical_action IS NULL AND witness_prune_intent_sequence IS NULL
         AND witness_prune_intent_epoch IS NULL AND witness_prune_intent_hash IS NULL
         AND witness_prune_cutoff_sequence IS NULL AND witness_prune_cutoff_hash IS NULL
         AND witness_prune_target_count IS NULL AND witness_prune_target_digest IS NULL
         AND witness_prune_copy_inventory_sha256 IS NULL
         AND witness_prune_authority_revision IS NULL AND witness_prune_authority_hash IS NULL
         AND witness_prune_valid_until IS NULL)
        OR (witness_prune_event_id IS NOT NULL AND purge_event_id IS NOT NULL
            AND witness_prune_candidate_sha256 IS NOT NULL
            AND witness_prune_canonical_action IS NOT NULL
            AND witness_prune_intent_sequence IS NOT NULL
            AND witness_prune_intent_epoch IS NOT NULL AND witness_prune_intent_hash IS NOT NULL
            AND witness_prune_cutoff_sequence IS NOT NULL AND witness_prune_cutoff_hash IS NOT NULL
            AND witness_prune_target_count IS NOT NULL AND witness_prune_target_digest IS NOT NULL
            AND witness_prune_copy_inventory_sha256 IS NOT NULL
            AND witness_prune_authority_revision IS NOT NULL
            AND witness_prune_authority_hash IS NOT NULL AND witness_prune_valid_until IS NOT NULL
            AND witness_prune_intent_sequence > witness_prune_cutoff_sequence
            AND witness_prune_candidate_sha256 = sha256(witness_prune_canonical_action))
    ),
    CONSTRAINT case_erasure_terminal_phase CHECK (
        (phase IN ('ERASURE_REQUESTED','ERASURE_PENDING')
            AND copy_absence_event_id IS NULL AND suppression_final_event_id IS NULL)
        OR (phase = 'PAYLOAD_ERASED_SUPPRESSION_RETAINED'
            AND copy_absence_event_id IS NOT NULL AND suppression_final_event_id IS NULL
            AND retention_policy_id IS NOT NULL AND suppression_until IS NOT NULL)
        OR (phase = 'ERASURE_FINAL'
            AND copy_absence_event_id IS NOT NULL AND suppression_final_event_id IS NOT NULL
            AND retention_policy_id IS NOT NULL AND suppression_until IS NOT NULL)
    )
);

CREATE TABLE claimcore.case_erasure_operation_denials (
    operation_commitment bytea PRIMARY KEY CHECK (octet_length(operation_commitment) = 32),
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    knowledge text NOT NULL CHECK (knowledge IN (
        'ACCEPTED', 'REVOKED', 'PENDING', 'ATTEMPT_UNCERTAIN', 'WITNESSED_INTENT'
    )),
    witness_intent_sequence bigint CHECK (
        witness_intent_sequence IS NULL OR witness_intent_sequence > 0
    ),
    witness_intent_epoch bigint CHECK (
        witness_intent_epoch IS NULL OR witness_intent_epoch > 0
    ),
    witness_intent_entry_hash bytea CHECK (
        witness_intent_entry_hash IS NULL OR octet_length(witness_intent_entry_hash) = 32
    ),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT case_erasure_denials_witness_group CHECK (
        (witness_intent_sequence IS NULL AND witness_intent_epoch IS NULL
         AND witness_intent_entry_hash IS NULL)
        OR (witness_intent_sequence IS NOT NULL AND witness_intent_epoch IS NOT NULL
            AND witness_intent_entry_hash IS NOT NULL)
    ),
    UNIQUE (witness_intent_epoch, witness_intent_sequence)
);
CREATE INDEX case_erasure_denials_by_case
    ON claimcore.case_erasure_operation_denials (case_id, operation_commitment);
