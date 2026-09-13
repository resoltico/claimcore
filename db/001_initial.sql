-- Initial schema for a fresh ClaimCore installation.
-- The migration runner creates claimcore.schema_migrations and records this script atomically.

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
        'RECORD_PAYMENT', 'CLEAR_PAYMENT', 'CLOSE', 'REOPEN'
    )),
    request_format_version smallint NOT NULL CHECK (request_format_version = 1),
    request_sha256 text NOT NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    snapshot_version smallint NOT NULL CHECK (snapshot_version = 2),
    snapshot jsonb NOT NULL CHECK (jsonb_typeof(snapshot) = 'object'),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    recorded_by text NOT NULL DEFAULT session_user,
    UNIQUE (case_reference, revision)
);

REVOKE ALL ON ALL TABLES IN SCHEMA claimcore FROM PUBLIC;
GRANT USAGE ON SCHEMA claimcore TO claimcore_app;
GRANT SELECT ON claimcore.schema_migrations TO claimcore_app;
GRANT SELECT, INSERT, UPDATE ON claimcore.cases TO claimcore_app;
GRANT SELECT, INSERT ON claimcore.case_changes TO claimcore_app;
