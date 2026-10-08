
-- A steward's exact terminal draft approval is authority, not evidence that a location is
-- absent. The schema-owner re-verifies signed evidence and consumes two distinct approvals.
CREATE TABLE claimcore.case_erasure_terminal_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    terminal_event_id uuid NOT NULL CHECK (
        terminal_event_id <> '00000000-0000-0000-0000-000000000000'
    ),
    case_id uuid NOT NULL,
    action_name text NOT NULL CHECK (action_name IN (
        'CONFIRM_MANAGED_PAYLOAD_ABSENCE','COMPLETE_SUPPRESSION_HORIZON'
    )),
    prune_event_id uuid NOT NULL,
    expected_authority_revision bigint NOT NULL CHECK (expected_authority_revision >= 0),
    expected_authority_hash bytea NOT NULL CHECK (octet_length(expected_authority_hash) = 32),
    installation_id uuid NOT NULL,
    lineage_id uuid NOT NULL,
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_cutoff_sequence bigint NOT NULL CHECK (witness_cutoff_sequence > 0),
    witness_cutoff_hash bytea NOT NULL CHECK (octet_length(witness_cutoff_hash) = 32),
    copy_inventory_digest bytea NOT NULL CHECK (octet_length(copy_inventory_digest) = 32),
    relevant_copy_count bigint NOT NULL CHECK (relevant_copy_count >= 0),
    expected_writer_generation bigint NOT NULL CHECK (expected_writer_generation > 0),
    recovery_fence_digest bytea CHECK (
        recovery_fence_digest IS NULL OR octet_length(recovery_fence_digest) = 32
    ),
    old_writer_generation bigint CHECK (old_writer_generation IS NULL OR old_writer_generation > 0),
    new_writer_generation bigint CHECK (new_writer_generation IS NULL OR new_writer_generation > 0),
    policy_id text NOT NULL CHECK (
        char_length(policy_id) BETWEEN 1 AND 128
        AND policy_id = btrim(policy_id) AND policy_id !~ '[[:cntrl:]]'
    ),
    suppression_until timestamptz NOT NULL,
    valid_until timestamptz NOT NULL,
    approver_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    approver_grant_revision bigint NOT NULL CHECK (approver_grant_revision > 0),
    approved_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > approved_at),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    approval_witness_epoch bigint NOT NULL CHECK (approval_witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    FOREIGN KEY (case_id,prune_event_id)
        REFERENCES claimcore.case_erasure_tombstones(case_id,witness_prune_event_id),
    UNIQUE (terminal_event_id,approver_actor_id),
    UNIQUE (case_id,approval_id),
    UNIQUE (approval_witness_epoch,witness_sequence),
    CONSTRAINT case_erasure_terminal_approval_shape CHECK (
        (action_name = 'CONFIRM_MANAGED_PAYLOAD_ABSENCE'
            AND recovery_fence_digest IS NULL
            AND old_writer_generation IS NULL AND new_writer_generation IS NULL)
        OR (action_name = 'COMPLETE_SUPPRESSION_HORIZON'
            AND recovery_fence_digest IS NOT NULL
            AND old_writer_generation IS NOT NULL AND new_writer_generation IS NOT NULL
            AND old_writer_generation < 9223372036854775807
            AND new_writer_generation = old_writer_generation + 1
            AND expected_writer_generation = new_writer_generation)
    ),
    CONSTRAINT case_erasure_terminal_approval_time CHECK (
        valid_until > approved_at
        AND valid_until <= approved_at + interval '24 hours'
        AND expires_at <= valid_until
    )
);
CREATE INDEX case_erasure_terminal_approvals_by_event
    ON claimcore.case_erasure_terminal_approvals(terminal_event_id,approver_actor_id);

-- The owner process is the technical executor. These append-only receipts retain only
-- pseudonymous authority, copy and recovery-fence evidence after claimant payload is gone.
CREATE TABLE claimcore.case_erasure_terminal_events (
    terminal_event_id uuid PRIMARY KEY CHECK (
        terminal_event_id <> '00000000-0000-0000-0000-000000000000'
    ),
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    action_name text NOT NULL CHECK (action_name IN (
        'CONFIRM_MANAGED_PAYLOAD_ABSENCE','COMPLETE_SUPPRESSION_HORIZON'
    )),
    resulting_phase text NOT NULL CHECK (resulting_phase IN (
        'PAYLOAD_ERASED_SUPPRESSION_RETAINED','ERASURE_FINAL'
    )),
    executor_kind text NOT NULL CHECK (executor_kind = 'SCHEMA_OWNER_PROCESS'),
    policy_id text NOT NULL CHECK (
        char_length(policy_id) BETWEEN 1 AND 128
        AND policy_id = btrim(policy_id) AND policy_id !~ '[[:cntrl:]]'
    ),
    suppression_until timestamptz NOT NULL,
    prune_event_id uuid NOT NULL,
    copy_inventory_digest bytea NOT NULL CHECK (octet_length(copy_inventory_digest) = 32),
    relevant_copy_count bigint NOT NULL CHECK (relevant_copy_count >= 0),
    writer_generation bigint NOT NULL CHECK (writer_generation > 0),
    copy_absence_seal_sha256 bytea NOT NULL CHECK (octet_length(copy_absence_seal_sha256) = 32),
    recovery_fence_digest bytea CHECK (
        recovery_fence_digest IS NULL OR octet_length(recovery_fence_digest) = 32
    ),
    approval_one_id uuid NOT NULL,
    approval_two_id uuid NOT NULL,
    actor_authority_revision bigint NOT NULL CHECK (actor_authority_revision > 0),
    previous_authority_revision bigint NOT NULL CHECK (previous_authority_revision >= 0),
    previous_authority_hash bytea NOT NULL CHECK (octet_length(previous_authority_hash) = 32),
    authority_revision bigint NOT NULL CHECK (authority_revision = previous_authority_revision + 1),
    authority_hash bytea NOT NULL CHECK (octet_length(authority_hash) = 32),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 16384),
    candidate_sha256 bytea NOT NULL CHECK (
        octet_length(candidate_sha256) = 32 AND candidate_sha256 = sha256(canonical_action)
    ),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    recorded_at timestamptz NOT NULL,
    UNIQUE (case_id,terminal_event_id),
    UNIQUE (case_id,action_name),
    UNIQUE (case_id,authority_revision),
    UNIQUE (witness_epoch,witness_sequence),
    CHECK (approval_one_id <> approval_two_id),
    CONSTRAINT case_erasure_terminal_event_phase CHECK (
        (action_name = 'CONFIRM_MANAGED_PAYLOAD_ABSENCE'
            AND resulting_phase = 'PAYLOAD_ERASED_SUPPRESSION_RETAINED'
            AND recovery_fence_digest IS NULL)
        OR (action_name = 'COMPLETE_SUPPRESSION_HORIZON'
            AND resulting_phase = 'ERASURE_FINAL'
            AND recovery_fence_digest IS NOT NULL)
    ),
    FOREIGN KEY (case_id,prune_event_id)
        REFERENCES claimcore.case_erasure_tombstones(case_id,witness_prune_event_id),
    FOREIGN KEY (case_id,approval_one_id)
        REFERENCES claimcore.case_erasure_terminal_approvals(case_id,approval_id),
    FOREIGN KEY (case_id,approval_two_id)
        REFERENCES claimcore.case_erasure_terminal_approvals(case_id,approval_id)
);

CREATE TABLE claimcore.case_erasure_terminal_approval_uses (
    approval_id uuid PRIMARY KEY,
    terminal_event_id uuid NOT NULL,
    case_id uuid NOT NULL,
    slot integer NOT NULL CHECK (slot IN (1,2)),
    used_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (terminal_event_id,slot),
    FOREIGN KEY (case_id,approval_id)
        REFERENCES claimcore.case_erasure_terminal_approvals(case_id,approval_id),
    FOREIGN KEY (case_id,terminal_event_id)
        REFERENCES claimcore.case_erasure_terminal_events(case_id,terminal_event_id)
);

ALTER TABLE claimcore.case_erasure_tombstones
    ADD CONSTRAINT case_erasure_copy_absence_event_fk
    FOREIGN KEY (case_id,copy_absence_event_id)
    REFERENCES claimcore.case_erasure_terminal_events(case_id,terminal_event_id);
ALTER TABLE claimcore.case_erasure_tombstones
    ADD CONSTRAINT case_erasure_suppression_final_event_fk
    FOREIGN KEY (case_id,suppression_final_event_id)
    REFERENCES claimcore.case_erasure_terminal_events(case_id,terminal_event_id);
