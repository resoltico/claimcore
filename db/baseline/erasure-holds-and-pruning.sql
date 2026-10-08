
-- These exact witnessed approvals survive deletion of raw-reference lifecycle drafts. Actor IDs
-- and opaque case IDs remain pseudonymous personal-data risks under the suppression policy.
CREATE TABLE claimcore.case_erasure_purge_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    purge_event_id uuid NOT NULL,
    case_id uuid NOT NULL,
    approver_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    approver_grant_revision bigint NOT NULL CHECK (approver_grant_revision > 0),
    approved_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > approved_at),
    draft_commitment bytea NOT NULL CHECK (octet_length(draft_commitment) = 32),
    approval_commitment bytea NOT NULL CHECK (octet_length(approval_commitment) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    FOREIGN KEY (case_id, purge_event_id)
        REFERENCES claimcore.case_erasure_tombstones(case_id, purge_event_id),
    UNIQUE (purge_event_id, approver_actor_id),
    UNIQUE (witness_epoch, witness_sequence)
);

-- Tombstone-scoped authority survives live claimant-row deletion. Only witnessed hold changes
-- advance this technical tip; approvals bind the current tip without advancing it.
CREATE TABLE claimcore.case_erasure_authority_tip (
    case_id uuid PRIMARY KEY REFERENCES claimcore.case_erasure_tombstones(case_id),
    revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
    event_hash bytea NOT NULL DEFAULT decode(repeat('00',32),'hex')
        CHECK (octet_length(event_hash) = 32)
);

-- Closed codes avoid recreating claimant free text after live purge. Record/release retain
-- separate actor, grant, witness and hash-chain evidence; review dates never auto-release.
CREATE TABLE claimcore.case_erasure_holds (
    hold_id uuid PRIMARY KEY CHECK (hold_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    ground_code text NOT NULL CHECK (ground_code IN (
        'LEGAL_RETENTION','REGULATORY_HOLD','DISPUTE','SECURITY_INCIDENT'
    )),
    review_on date NOT NULL,
    recorded_by uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    recorded_grant_revision bigint NOT NULL CHECK (recorded_grant_revision > 0),
    recorded_at timestamptz NOT NULL,
    record_event_id uuid NOT NULL UNIQUE,
    record_revision bigint NOT NULL CHECK (record_revision > 0),
    record_previous_hash bytea NOT NULL CHECK (octet_length(record_previous_hash) = 32),
    record_event_hash bytea NOT NULL CHECK (octet_length(record_event_hash) = 32),
    record_canonical_action bytea NOT NULL CHECK (octet_length(record_canonical_action) BETWEEN 1 AND 4096),
    record_candidate_sha256 bytea NOT NULL CHECK (octet_length(record_candidate_sha256) = 32),
    record_witness_sequence bigint NOT NULL CHECK (record_witness_sequence > 0),
    record_witness_epoch bigint NOT NULL CHECK (record_witness_epoch > 0),
    record_witness_hash bytea NOT NULL CHECK (octet_length(record_witness_hash) = 32),
    UNIQUE (case_id, record_revision),
    UNIQUE (record_witness_epoch, record_witness_sequence)
);

CREATE TABLE claimcore.case_erasure_hold_releases (
    release_event_id uuid PRIMARY KEY CHECK (release_event_id <> '00000000-0000-0000-0000-000000000000'),
    hold_id uuid NOT NULL UNIQUE REFERENCES claimcore.case_erasure_holds(hold_id),
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    release_code text NOT NULL CHECK (release_code IN (
        'LEGAL_RELEASE','REVIEW_CLOSED','EXPIRED_WITH_REVIEW'
    )),
    released_by uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    released_grant_revision bigint NOT NULL CHECK (released_grant_revision > 0),
    released_at timestamptz NOT NULL,
    release_revision bigint NOT NULL CHECK (release_revision > 0),
    release_previous_hash bytea NOT NULL CHECK (octet_length(release_previous_hash) = 32),
    release_event_hash bytea NOT NULL CHECK (octet_length(release_event_hash) = 32),
    release_canonical_action bytea NOT NULL CHECK (
        octet_length(release_canonical_action) BETWEEN 1 AND 4096
    ),
    release_candidate_sha256 bytea NOT NULL CHECK (octet_length(release_candidate_sha256) = 32),
    release_witness_sequence bigint NOT NULL CHECK (release_witness_sequence > 0),
    release_witness_epoch bigint NOT NULL CHECK (release_witness_epoch > 0),
    release_witness_hash bytea NOT NULL CHECK (octet_length(release_witness_hash) = 32),
    UNIQUE (case_id,release_revision),
    UNIQUE (release_witness_epoch,release_witness_sequence)
);
CREATE INDEX case_erasure_holds_by_case ON claimcore.case_erasure_holds(case_id,hold_id);
CREATE INDEX case_erasure_hold_releases_by_case
    ON claimcore.case_erasure_hold_releases(case_id,hold_id);

CREATE TABLE claimcore.case_erasure_prune_approvals (
    approval_id uuid PRIMARY KEY CHECK (approval_id <> '00000000-0000-0000-0000-000000000000'),
    case_id uuid NOT NULL REFERENCES claimcore.case_erasure_tombstones(case_id),
    prune_event_id uuid NOT NULL CHECK (prune_event_id <> '00000000-0000-0000-0000-000000000000'),
    purge_event_id uuid NOT NULL,
    cutoff_sequence bigint NOT NULL CHECK (cutoff_sequence > 0),
    cutoff_hash bytea NOT NULL CHECK (octet_length(cutoff_hash) = 32),
    target_count bigint NOT NULL CHECK (target_count > 0),
    target_digest bytea NOT NULL CHECK (octet_length(target_digest) = 32),
    expected_authority_revision bigint NOT NULL CHECK (expected_authority_revision >= 0),
    expected_authority_hash bytea NOT NULL CHECK (octet_length(expected_authority_hash) = 32),
    valid_until timestamptz NOT NULL,
    approver_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    approver_grant_revision bigint NOT NULL CHECK (approver_grant_revision > 0),
    approved_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > approved_at),
    canonical_action bytea NOT NULL CHECK (octet_length(canonical_action) BETWEEN 1 AND 8192),
    candidate_sha256 bytea NOT NULL CHECK (octet_length(candidate_sha256) = 32),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    FOREIGN KEY (case_id,purge_event_id)
        REFERENCES claimcore.case_erasure_tombstones(case_id,purge_event_id),
    UNIQUE (prune_event_id,approver_actor_id),
    UNIQUE (witness_epoch,witness_sequence)
);

CREATE TABLE claimcore.case_erasure_prune_targets (
    case_id uuid NOT NULL,
    prune_event_id uuid NOT NULL,
    sequence bigint NOT NULL CHECK (sequence > 0),
    operation_id uuid NOT NULL CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    phase text NOT NULL CHECK (phase IN (
        'INTENT','SETTLED_ACCEPTED','SETTLED_REVOKED','SETTLED_AUTHORITY','ABORTED_BEFORE_COMMIT'
    )),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    entry_hash bytea NOT NULL CHECK (octet_length(entry_hash) = 32),
    payload_sha256 bytea NOT NULL CHECK (octet_length(payload_sha256) = 32),
    is_external_publication boolean NOT NULL,
    PRIMARY KEY (case_id,sequence),
    FOREIGN KEY (case_id,prune_event_id)
        REFERENCES claimcore.case_erasure_tombstones(case_id,witness_prune_event_id)
);
CREATE INDEX case_erasure_prune_targets_by_event
    ON claimcore.case_erasure_prune_targets(prune_event_id,sequence);
