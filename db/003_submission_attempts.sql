-- Append-only evidence for every execution attempt made from retained recovery material.
-- Definite outcomes are technical settlements, not accepted claim history.

CREATE TABLE claimcore.request_submission_attempts (
    attempt_id uuid PRIMARY KEY
        CHECK (attempt_id <> '00000000-0000-0000-0000-000000000000'),
    operation_id uuid NOT NULL
        REFERENCES claimcore.request_preparations(operation_id) ON DELETE CASCADE,
    started_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX request_submission_attempts_by_operation
    ON claimcore.request_submission_attempts (operation_id, started_at, attempt_id);

CREATE TABLE claimcore.request_submission_settlements (
    attempt_id uuid PRIMARY KEY
        REFERENCES claimcore.request_submission_attempts(attempt_id) ON DELETE CASCADE,
    outcome text NOT NULL CHECK (outcome IN ('ACCEPTED', 'REJECTED', 'ERROR')),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- A pre-003 start has no attempt identity that can be settled safely. Preserve that uncertainty even
-- if a later, independently identified retry receives a definite result.
CREATE TABLE claimcore.request_submission_legacy_uncertainty (
    operation_id uuid PRIMARY KEY
        REFERENCES claimcore.request_preparations(operation_id) ON DELETE CASCADE
);

INSERT INTO claimcore.request_submission_legacy_uncertainty (operation_id)
SELECT operation_id
FROM claimcore.request_preparation_lifecycle
WHERE state = 'SUBMISSION_STARTED';

REVOKE ALL ON TABLE claimcore.request_submission_attempts FROM PUBLIC;
REVOKE ALL ON TABLE claimcore.request_submission_settlements FROM PUBLIC;
REVOKE ALL ON TABLE claimcore.request_submission_legacy_uncertainty FROM PUBLIC;

GRANT SELECT, INSERT ON claimcore.request_submission_attempts TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.request_submission_settlements TO claimcore_app;
