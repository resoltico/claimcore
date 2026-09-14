-- Durable operation authority and installation-wide business-time configuration.
-- This migration deliberately preserves every pre-006 preparation, attempt, settlement, receipt,
-- and lineage value. Dismissals that were already pruned cannot be reconstructed because their
-- operation identity was not retained by the earlier prune journal.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM claimcore.request_preparation_lifecycle lifecycle
        JOIN claimcore.case_changes accepted
            ON accepted.operation_id = lifecycle.operation_id
        WHERE lifecycle.state = 'DISMISSED'
    ) THEN
        RAISE EXCEPTION 'Cannot backfill contradictory accepted and dismissed operation authority.';
    END IF;
END $$;

CREATE TABLE claimcore.operation_revocations (
    operation_id uuid PRIMARY KEY
        CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    canonical_request_format smallint NOT NULL CHECK (canonical_request_format = 2),
    request_sha256 text NOT NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    revoked_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    reason text NOT NULL CHECK (reason IN ('OPERATOR_DISMISSAL', 'LEGACY_DISMISSAL'))
);

CREATE INDEX operation_revocations_by_revoked_at
    ON claimcore.operation_revocations (revoked_at, operation_id);

CREATE INDEX request_preparations_by_prepared_at
    ON claimcore.request_preparations (prepared_at, operation_id);

-- Only retained pre-006 dismissals still carry enough identity to close future authority.
INSERT INTO claimcore.operation_revocations (
    operation_id, canonical_request_format, request_sha256, revoked_at, reason
)
SELECT
    preparation.operation_id,
    preparation.canonical_request_format,
    preparation.request_sha256,
    lifecycle.recorded_at,
    'LEGACY_DISMISSAL'
FROM claimcore.request_preparations preparation
JOIN claimcore.request_preparation_lifecycle lifecycle
    ON lifecycle.operation_id = preparation.operation_id
WHERE lifecycle.state = 'DISMISSED';

ALTER TABLE claimcore.request_submission_settlements
    DROP CONSTRAINT request_submission_settlements_outcome_check;
ALTER TABLE claimcore.request_submission_settlements
    ADD CONSTRAINT request_submission_settlements_outcome_check
    CHECK (outcome IN ('ACCEPTED', 'REJECTED', 'ERROR', 'REVOKED_BEFORE_EXECUTION'));

ALTER TABLE claimcore.case_changes
    DROP CONSTRAINT case_changes_command_name_check;
ALTER TABLE claimcore.case_changes
    ADD CONSTRAINT case_changes_command_name_check
    CHECK (command_name IN (
        'OPEN', 'AMEND_REGISTRATION', 'CORRECT_CASE', 'DECIDE', 'WITHDRAW_DECISION',
        'RECORD_PAYMENT', 'CLEAR_PAYMENT', 'CLOSE', 'REOPEN'
    ));

ALTER TABLE claimcore.installation_lineage
    ADD COLUMN business_time_zone text;
ALTER TABLE claimcore.installation_lineage
    ADD CONSTRAINT installation_lineage_business_time_zone_shape
    CHECK (
        business_time_zone IS NULL OR (
            char_length(business_time_zone) BETWEEN 1 AND 128
            AND business_time_zone = btrim(business_time_zone)
            AND business_time_zone !~ '[[:cntrl:]]'
        )
    );

REVOKE ALL ON TABLE claimcore.operation_revocations FROM PUBLIC;
GRANT SELECT, INSERT ON TABLE claimcore.operation_revocations TO claimcore_app;
