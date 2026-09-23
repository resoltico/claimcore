-- Current installation baseline. Executed only by SchemaBaseline.initialize in one transaction.
-- No historical installation may be adopted, upgraded, reset or repaired by this script.
CREATE SCHEMA claimcore;
REVOKE ALL ON SCHEMA claimcore FROM PUBLIC;

CREATE TABLE claimcore.schema_baseline (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    baseline_id text NOT NULL CHECK (baseline_id ~ '^[a-z][a-z0-9-]{0,63}$'),
    script_sha256 text NOT NULL CHECK (script_sha256 ~ '^[0-9a-f]{64}$'),
    installed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    installed_by text NOT NULL DEFAULT session_user
);

CREATE TABLE claimcore.cases (
    incident_date date NOT NULL,
    incident_notification_date date NOT NULL,
    incident_country text NOT NULL,
    claimant_name text NOT NULL,
    insurer_name text NOT NULL,
    claimed_amount numeric NOT NULL,
    claimed_currency text NOT NULL,
    case_reference text COLLATE "C" PRIMARY KEY,
    payment_decision_date date,
    payable_amount numeric,
    payable_currency text,
    payment_date date,
    status text NOT NULL CHECK (status IN ('OPENED', 'CLOSED')),
    revision bigint NOT NULL CHECK (revision > 0),
    CONSTRAINT reference_shape CHECK (
        char_length(case_reference) BETWEEN 1 AND 80
        AND btrim(case_reference) = case_reference
        AND case_reference !~ '[[:cntrl:]]'
    ),
    CONSTRAINT name_shape CHECK (
        char_length(btrim(incident_country)) BETWEEN 1 AND 100
        AND char_length(btrim(claimant_name)) BETWEEN 1 AND 200
        AND char_length(btrim(insurer_name)) BETWEEN 1 AND 200
        AND incident_country = btrim(incident_country)
        AND claimant_name = btrim(claimant_name)
        AND insurer_name = btrim(insurer_name)
        AND incident_country !~ '[[:cntrl:]]'
        AND claimant_name !~ '[[:cntrl:]]'
        AND insurer_name !~ '[[:cntrl:]]'
    ),
    CONSTRAINT claimed_money CHECK (
        claimed_amount >= 0 AND claimed_amount <= 999999999999999999.9999
        AND scale(claimed_amount) <= 4 AND claimed_currency ~ '^[A-Z]{3}$'
    ),
    CONSTRAINT decision_group CHECK (
        (payment_decision_date IS NULL AND payable_amount IS NULL AND payable_currency IS NULL)
        OR (payment_decision_date IS NOT NULL AND payable_amount IS NOT NULL AND payable_currency IS NOT NULL)
    ),
    CONSTRAINT payable_money CHECK (
        payable_amount IS NULL OR (
            payable_amount >= 0 AND payable_amount <= 999999999999999999.9999
            AND scale(payable_amount) <= 4 AND payable_currency ~ '^[A-Z]{3}$'
        )
    ),
    CONSTRAINT date_bounds CHECK (
        incident_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'
        AND incident_notification_date BETWEEN incident_date AND DATE '9999-12-31'
        AND (payment_decision_date IS NULL OR payment_decision_date BETWEEN incident_notification_date AND DATE '9999-12-31')
        AND (payment_date IS NULL OR payment_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31')
    ),
    CONSTRAINT paid_requires_decision CHECK (
        payment_date IS NULL OR (
            payment_decision_date IS NOT NULL AND payable_amount > 0 AND payment_date >= payment_decision_date
        )
    )
);

CREATE TABLE claimcore.case_changes (
    operation_id uuid PRIMARY KEY CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    case_reference text COLLATE "C" NOT NULL REFERENCES claimcore.cases(case_reference),
    revision bigint NOT NULL CHECK (revision > 0),
    command_name text NOT NULL CHECK (command_name IN (
        'OPEN', 'AMEND_REGISTRATION', 'DECIDE', 'WITHDRAW_DECISION',
        'RECORD_PAYMENT', 'CLEAR_PAYMENT', 'CLOSE', 'REOPEN', 'CORRECT_CASE'
    )),
    request_format_version smallint NOT NULL CHECK (request_format_version = 1),
    request_sha256 text NOT NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    snapshot_version smallint NOT NULL CHECK (snapshot_version = 2),
    snapshot jsonb NOT NULL CHECK (jsonb_typeof(snapshot) = 'object'),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    recorded_by text NOT NULL DEFAULT session_user,
    UNIQUE (case_reference, revision)
);

CREATE TABLE claimcore.installation_lineage (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    lineage_id uuid NOT NULL UNIQUE
        DEFAULT gen_random_uuid()
        CHECK (lineage_id <> '00000000-0000-0000-0000-000000000000'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    business_time_zone text NOT NULL,
    CONSTRAINT installation_lineage_business_time_zone_shape CHECK (
        char_length(business_time_zone) BETWEEN 1 AND 128
        AND business_time_zone = btrim(business_time_zone)
        AND business_time_zone !~ '[[:cntrl:]]'
    )
);


CREATE TABLE claimcore.request_preparations (
    operation_id uuid PRIMARY KEY
        CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
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
    preparing_contract_kind text NOT NULL CHECK (preparing_contract_kind IN ('SEMANTIC_CORE_V1', 'CANONICAL_RECORD_V3'))
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
    operation_id uuid NOT NULL
        REFERENCES claimcore.request_preparations(operation_id) ON DELETE CASCADE,
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
    canonical_request_format smallint NOT NULL CHECK (canonical_request_format = 3),
    request_sha256 text NOT NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    revoked_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    reason text NOT NULL CHECK (reason = 'OPERATOR_DISMISSAL')
);

CREATE INDEX operation_revocations_by_revoked_at
    ON claimcore.operation_revocations (revoked_at, operation_id);

CREATE INDEX request_preparations_by_prepared_at
    ON claimcore.request_preparations (prepared_at, operation_id);

REVOKE ALL ON ALL TABLES IN SCHEMA claimcore FROM PUBLIC, claimcore_app;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA claimcore FROM PUBLIC, claimcore_app;
GRANT USAGE ON SCHEMA claimcore TO claimcore_app;
GRANT SELECT ON claimcore.schema_baseline, claimcore.installation_lineage TO claimcore_app;
GRANT SELECT, INSERT, UPDATE ON claimcore.cases TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_changes, claimcore.request_preparations,
    claimcore.request_preparation_lifecycle, claimcore.request_submission_attempts,
    claimcore.request_submission_settlements, claimcore.operation_revocations TO claimcore_app;
