ALTER TABLE claimcore.request_preparations
    RENAME COLUMN protocol_version TO canonical_request_format;
ALTER TABLE claimcore.request_preparations
    RENAME COLUMN web_contract_fingerprint TO preparing_contract_fingerprint;
ALTER TABLE claimcore.request_preparations
    ADD COLUMN preparing_contract_kind text;
UPDATE claimcore.request_preparations
SET preparing_contract_kind = 'LEGACY_UNCLASSIFIED';
ALTER TABLE claimcore.request_preparations
    ALTER COLUMN preparing_contract_kind SET NOT NULL;
ALTER TABLE claimcore.request_preparations
    ADD CONSTRAINT preparing_contract_kind_value
    CHECK (preparing_contract_kind IN ('LEGACY_UNCLASSIFIED', 'SEMANTIC_CORE_V1'));
