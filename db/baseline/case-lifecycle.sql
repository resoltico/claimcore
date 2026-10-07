
-- Case disposition, privacy and holds are technical authority outside CaseFields.
-- Every change to these projections is paired with an externally witnessed event.
CREATE TABLE claimcore.case_lifecycle_events (
    event_id uuid PRIMARY KEY CHECK (event_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL,
    case_reference text COLLATE "C" NOT NULL,
    lifecycle_sequence bigint NOT NULL CHECK (lifecycle_sequence > 0),
    business_revision bigint NOT NULL CHECK (business_revision > 0),
    action_name text NOT NULL CHECK (action_name IN (
        'VOID_DATA_ENTRY_ERROR', 'REINSTATE_VOIDED', 'REQUEST_ERASURE',
        'MARK_ERASURE_PENDING', 'RECORD_HOLD', 'RELEASE_HOLD',
        'PURGE_PAYLOAD', 'FINALIZE_ERASURE'
    )),
    actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    grant_revision bigint NOT NULL CHECK (grant_revision > 0),
    draft_sha256 bytea NOT NULL CHECK (octet_length(draft_sha256) = 32),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 16384),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    event_hash bytea NOT NULL CHECK (octet_length(event_hash) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (case_id, lifecycle_sequence),
    UNIQUE (witness_epoch, witness_sequence),
    FOREIGN KEY (case_id, case_reference) REFERENCES claimcore.cases(case_id, case_reference)
);

-- Approvals are separate authenticated, witnessed acts. A stored approver ID is never inferred
-- from a display name, IdP group, or an executor-supplied array at decision time.
CREATE TABLE claimcore.case_lifecycle_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    operation_id uuid NOT NULL CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.cases(case_id),
    action_name text NOT NULL CHECK (action_name IN (
        'VOID_DATA_ENTRY_ERROR', 'REINSTATE_VOIDED', 'PURGE_PAYLOAD', 'FINALIZE_ERASURE'
    )),
    expected_revision bigint NOT NULL CHECK (expected_revision > 0),
    expected_lifecycle_sequence bigint NOT NULL CHECK (expected_lifecycle_sequence >= 0),
    expected_lifecycle_hash bytea NOT NULL CHECK (octet_length(expected_lifecycle_hash) = 32),
    event_digest bytea NOT NULL CHECK (octet_length(event_digest) = 32),
    approver_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    approver_grant_revision bigint NOT NULL CHECK (approver_grant_revision > 0),
    approved_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > approved_at),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    UNIQUE (operation_id, approver_actor_id),
    UNIQUE (witness_epoch, witness_sequence)
);

CREATE TABLE claimcore.case_holds (
    hold_id uuid PRIMARY KEY CHECK (hold_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.cases(case_id),
    ground text NOT NULL CHECK (
        char_length(ground) BETWEEN 1 AND 500 AND ground = btrim(ground)
        AND ground !~ '[[:cntrl:]]'
    ),
    review_on date NOT NULL,
    recorded_by uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    recorded_at timestamptz NOT NULL,
    released_by uuid REFERENCES claimcore.actors(actor_id),
    released_at timestamptz,
    release_reason text CHECK (
        release_reason IS NULL OR (
            char_length(release_reason) BETWEEN 1 AND 500
            AND release_reason = btrim(release_reason)
            AND release_reason !~ '[[:cntrl:]]'
        )
    ),
    CONSTRAINT case_holds_release_group CHECK (
        (released_by IS NULL AND released_at IS NULL AND release_reason IS NULL)
        OR (released_by IS NOT NULL AND released_at IS NOT NULL AND release_reason IS NOT NULL)
    )
);
CREATE INDEX case_holds_active_by_case ON claimcore.case_holds (case_id, hold_id)
    WHERE released_at IS NULL;
