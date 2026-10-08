
CREATE TABLE claimcore_witness.journal (
    installation_id uuid NOT NULL,
    lineage_id uuid NOT NULL,
    epoch bigint NOT NULL CHECK (epoch > 0),
    sequence bigint NOT NULL CHECK (sequence > 0),
    operation_id uuid NOT NULL,
    scope_kind text NOT NULL CHECK (scope_kind IN ('INSTALLATION', 'CASE')),
    subject_case_id uuid,
    CONSTRAINT journal_scope_subject_check CHECK (
        (scope_kind = 'CASE' AND subject_case_id IS NOT NULL
            AND subject_case_id <> '00000000-0000-0000-0000-000000000000'::uuid)
        OR (scope_kind = 'INSTALLATION' AND subject_case_id IS NULL)
    ),
    key_id uuid NOT NULL,
    phase text NOT NULL CHECK (phase IN (
        'INTENT', 'SETTLED_ACCEPTED', 'SETTLED_REVOKED',
        'SETTLED_AUTHORITY', 'ABORTED_BEFORE_COMMIT', 'KEY_ROTATED'
    )),
    payload_sha256 bytea NOT NULL CHECK (octet_length(payload_sha256) = 32),
    previous_hash bytea NOT NULL CHECK (octet_length(previous_hash) = 32),
    entry_hash bytea NOT NULL CHECK (octet_length(entry_hash) = 32),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (installation_id, sequence),
    UNIQUE (installation_id, operation_id, phase)
);

-- Hash-linked metadata remains immutable after an authorized privacy purge. Ciphertext is
-- separately owner-prunable; the writer cannot remove or rewrite either relation.
CREATE TABLE claimcore_witness.journal_payloads (
    installation_id uuid NOT NULL,
    sequence bigint NOT NULL,
    subject_case_id uuid,
    encrypted_payload bytea NOT NULL CHECK (octet_length(encrypted_payload) BETWEEN 1 AND 1048576),
    PRIMARY KEY (installation_id, sequence),
    FOREIGN KEY (installation_id, sequence)
        REFERENCES claimcore_witness.journal (installation_id, sequence)
);
CREATE INDEX journal_payloads_by_case
    ON claimcore_witness.journal_payloads (subject_case_id, installation_id, sequence)
    WHERE subject_case_id IS NOT NULL;

-- Owner-only handoff evidence is not writable by the runtime role. The pending fence is
-- co-committed with its journal INTENT; the new writer remains closed until settlement.
