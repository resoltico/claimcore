
ALTER TABLE claimcore.case_changes
    ADD FOREIGN KEY (preparer_actor_id) REFERENCES claimcore.actors(actor_id),
    ADD FOREIGN KEY (importer_actor_id) REFERENCES claimcore.actors(actor_id),
    ADD FOREIGN KEY (submitter_actor_id) REFERENCES claimcore.actors(actor_id),
    ADD FOREIGN KEY (resolver_actor_id) REFERENCES claimcore.actors(actor_id),
    ADD FOREIGN KEY (accepted_actor_id) REFERENCES claimcore.actors(actor_id);


CREATE TABLE claimcore.request_preparations (
    operation_id uuid PRIMARY KEY
        CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    witness_event_id uuid NOT NULL UNIQUE
        CHECK (witness_event_id <> '00000000-0000-0000-0000-000000000000'),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    witness_candidate_sha256 bytea NOT NULL CHECK (octet_length(witness_candidate_sha256) = 32),
    UNIQUE (witness_epoch, witness_sequence),
    case_id uuid NOT NULL CHECK (case_id <> '00000000-0000-0000-0000-000000000000'),
    preparer_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    importer_actor_id uuid REFERENCES claimcore.actors(actor_id),
    preparer_grant_revision bigint NOT NULL CHECK (preparer_grant_revision > 0),
    canonical_request_format smallint NOT NULL CHECK (canonical_request_format = 3),
    request_sha256 text NOT NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    canonical_request bytea NOT NULL
        CHECK (octet_length(canonical_request) BETWEEN 1 AND 65536),
    prepared_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    prepared_application_version text NOT NULL CHECK (
        char_length(prepared_application_version) BETWEEN 1 AND 200
        AND btrim(prepared_application_version) = prepared_application_version
    ),
    preparing_contract_fingerprint text NOT NULL CHECK (preparing_contract_fingerprint ~ '^[0-9a-f]{64}$'),
    preparing_contract_kind text NOT NULL CHECK (preparing_contract_kind = 'SEMANTIC_CORE_V1')
);

-- The first submission marker is append-only; revocation is separate durable authority.
CREATE TABLE claimcore.request_preparation_lifecycle (
    operation_id uuid PRIMARY KEY REFERENCES claimcore.request_preparations(operation_id) ON DELETE CASCADE,
    state text NOT NULL CHECK (state = 'SUBMISSION_STARTED'),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX request_preparation_lifecycle_retention
    ON claimcore.request_preparation_lifecycle (state, recorded_at);

-- Schema-owner pruning is recorded without retaining request bytes or claim payloads in the audit row.
CREATE TABLE claimcore.request_preparation_prunes (
    prune_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    executed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    executed_by text NOT NULL DEFAULT session_user,
    dry_run boolean NOT NULL,
    settled_retention_days integer NOT NULL CHECK (settled_retention_days BETWEEN 1 AND 3650),
    abandoned_retention_days integer NOT NULL CHECK (abandoned_retention_days BETWEEN 1 AND 3650),
    batch_limit integer NOT NULL CHECK (batch_limit BETWEEN 1 AND 1000),
    candidate_count integer NOT NULL CHECK (candidate_count >= 0),
    deleted_count integer NOT NULL CHECK (deleted_count BETWEEN 0 AND candidate_count),
    CHECK (NOT dry_run OR deleted_count = 0)
);

CREATE TABLE claimcore.request_submission_attempts (
    attempt_id uuid PRIMARY KEY
        CHECK (attempt_id <> '00000000-0000-0000-0000-000000000000'),
    attempt_ordinal bigint NOT NULL CHECK (attempt_ordinal > 0),
    witness_event_id uuid NOT NULL UNIQUE CHECK (witness_event_id = attempt_id),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    witness_candidate_sha256 bytea NOT NULL CHECK (octet_length(witness_candidate_sha256) = 32),
    UNIQUE (witness_epoch, witness_sequence),
    operation_id uuid NOT NULL
        REFERENCES claimcore.request_preparations(operation_id) ON DELETE CASCADE,
    UNIQUE (operation_id, attempt_ordinal),
    submitter_actor_id uuid REFERENCES claimcore.actors(actor_id),
    resolver_actor_id uuid REFERENCES claimcore.actors(actor_id),
    grant_revision bigint NOT NULL CHECK (grant_revision > 0),
    CONSTRAINT request_submission_attempts_actor_phase CHECK (
        (submitter_actor_id IS NOT NULL) <> (resolver_actor_id IS NOT NULL)
    ),
    started_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX request_submission_attempts_by_operation
    ON claimcore.request_submission_attempts (operation_id, started_at, attempt_id);

CREATE TABLE claimcore.request_submission_settlements (
    attempt_id uuid PRIMARY KEY
        REFERENCES claimcore.request_submission_attempts(attempt_id) ON DELETE CASCADE,
    outcome text NOT NULL CHECK (outcome IN ('ACCEPTED', 'REJECTED', 'ERROR', 'REVOKED_BEFORE_EXECUTION')),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE claimcore.operation_revocations (
    operation_id uuid PRIMARY KEY
        CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    witness_event_id uuid NOT NULL UNIQUE
        CHECK (witness_event_id <> '00000000-0000-0000-0000-000000000000'
            AND witness_event_id <> operation_id),
    case_id uuid NOT NULL CHECK (case_id <> '00000000-0000-0000-0000-000000000000'),
    canonical_request_format smallint NOT NULL CHECK (canonical_request_format = 3),
    request_sha256 text NOT NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    revoking_actor_id uuid NOT NULL REFERENCES claimcore.actors(actor_id),
    grant_revision bigint NOT NULL CHECK (grant_revision > 0),
    revoked_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    witness_sequence bigint NOT NULL CHECK (witness_sequence > 0),
    witness_epoch bigint NOT NULL CHECK (witness_epoch > 0),
    witness_entry_hash bytea NOT NULL CHECK (octet_length(witness_entry_hash) = 32),
    UNIQUE (witness_epoch, witness_sequence),
    reason text NOT NULL CHECK (reason = 'OPERATOR_DISMISSAL')
);

CREATE INDEX operation_revocations_by_revoked_at
    ON claimcore.operation_revocations (revoked_at, operation_id);

CREATE INDEX request_preparations_by_prepared_at
    ON claimcore.request_preparations (prepared_at, operation_id);
