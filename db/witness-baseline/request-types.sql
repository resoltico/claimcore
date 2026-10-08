CREATE TYPE claimcore_witness.installation_identity AS (
    installation_id uuid,
    lineage_id uuid,
    epoch bigint
);

CREATE TYPE claimcore_witness.journal_tip AS (
    sequence bigint,
    entry_hash bytea
);

CREATE TYPE claimcore_witness.journal_ticket AS (
    operation_id uuid,
    sequence bigint,
    entry_hash bytea
);

CREATE TYPE claimcore_witness.journal_subject AS (
    scope_kind text,
    subject_case_id uuid
);

CREATE TYPE claimcore_witness.journal_request AS (
    operation_id uuid,
    phase text,
    key_id uuid,
    encrypted_payload bytea
);

CREATE TYPE claimcore_witness.journal_payload AS (
    key_id uuid,
    encrypted_payload bytea
);

CREATE TYPE claimcore_witness.writer_delivery AS (
    key_id uuid,
    encrypted_payload bytea,
    writer_capability bytea
);

CREATE TYPE claimcore_witness.handoff_plan AS (
    handoff_id uuid,
    old_generation bigint,
    new_capability_sha256 bytea
);

CREATE TYPE claimcore_witness.handoff_approval AS (
    canonical bytea,
    signature bytea,
    signing_key_id uuid,
    approval_one_id uuid,
    approval_two_id uuid
);

CREATE TYPE claimcore_witness.writer_capabilities AS (
    old_capability bytea,
    new_capability bytea
);

CREATE TYPE claimcore_witness.signed_candidate AS (
    canonical bytea,
    signature bytea
);

CREATE TYPE claimcore_witness.writer_activation_plan AS (
    handoff_id uuid,
    activation_id uuid,
    canonical bytea
);

CREATE TYPE claimcore_witness.data_use_activation_plan AS (
    activation_id uuid,
    canonical bytea
);

CREATE TYPE claimcore_witness.activation_payloads AS (
    key_id uuid,
    candidate bytea,
    settlement bytea
);

CREATE TYPE claimcore_witness.handoff_abort_decision AS (
    canonical bytea,
    signature_one bytea,
    signature_two bytea,
    signing_key_one uuid,
    signing_key_two uuid
);

CREATE TYPE claimcore_witness.prune_source AS (
    case_id uuid,
    purge claimcore_witness.journal_ticket,
    cutoff claimcore_witness.journal_tip,
    target_count bigint,
    target_digest bytea
);

CREATE TYPE claimcore_witness.prune_authority AS (
    event_id uuid,
    intent claimcore_witness.journal_tip,
    approval_one claimcore_witness.journal_ticket,
    approval_two claimcore_witness.journal_ticket
);

CREATE TYPE claimcore_witness.key_rotation AS (
    operation_id uuid,
    old_key_id uuid,
    new_key_id uuid,
    key_check bytea
);

CREATE TYPE claimcore_witness.owner_signature AS (
    signature bytea,
    signer_id uuid,
    actor_id uuid
);

CREATE TYPE claimcore_witness.owner_signature_pair AS (
    first claimcore_witness.owner_signature,
    second claimcore_witness.owner_signature
);

CREATE TYPE claimcore_witness.loss_retirement_plan AS (
    retirement_id uuid,
    canonical bytea,
    operation_set_kind text,
    known_operation_count integer,
    known_operation_digest bytea
);

CREATE TYPE claimcore_witness.loss_settlement_proof AS (
    intent claimcore_witness.journal_ticket,
    primary_candidate_sha256 bytea
);
