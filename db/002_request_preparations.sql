-- Durable technical recovery material for exact protocol-v2 requests.
-- This does not add a claim field or an accepted claims-state transition.

CREATE TABLE claimcore.installation_lineage (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    lineage_id uuid NOT NULL UNIQUE
        DEFAULT gen_random_uuid()
        CHECK (lineage_id <> '00000000-0000-0000-0000-000000000000'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

INSERT INTO claimcore.installation_lineage DEFAULT VALUES;

CREATE TABLE claimcore.request_preparations (
    operation_id uuid PRIMARY KEY
        CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    protocol_version smallint NOT NULL CHECK (protocol_version = 2),
    request_sha256 text NOT NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    canonical_request bytea NOT NULL
        CHECK (octet_length(canonical_request) BETWEEN 1 AND 65536),
    prepared_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    prepared_application_version text NOT NULL CHECK (
        char_length(prepared_application_version) BETWEEN 1 AND 200
        AND btrim(prepared_application_version) = prepared_application_version
    ),
    web_contract_fingerprint text NOT NULL
        CHECK (web_contract_fingerprint ~ '^[0-9a-f]{64}$')
);

-- A preparation receives at most one immutable terminal technical marker. Its absence means it has
-- not yet been submitted or dismissed. INSERT-only markers make every lifecycle transition monotonic.
CREATE TABLE claimcore.request_preparation_lifecycle (
    operation_id uuid PRIMARY KEY REFERENCES claimcore.request_preparations(operation_id) ON DELETE CASCADE,
    state text NOT NULL CHECK (state IN ('SUBMISSION_STARTED', 'DISMISSED')),
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

REVOKE ALL ON TABLE claimcore.installation_lineage FROM PUBLIC;
REVOKE ALL ON TABLE claimcore.request_preparations FROM PUBLIC;
REVOKE ALL ON TABLE claimcore.request_preparation_lifecycle FROM PUBLIC;
REVOKE ALL ON TABLE claimcore.request_preparation_prunes FROM PUBLIC;
REVOKE ALL ON SEQUENCE claimcore.request_preparation_prunes_prune_id_seq FROM PUBLIC;

GRANT SELECT ON claimcore.installation_lineage TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.request_preparations TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.request_preparation_lifecycle TO claimcore_app;
