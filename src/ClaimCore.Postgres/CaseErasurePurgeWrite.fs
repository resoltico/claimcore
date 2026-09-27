namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal ErasurePurgeCommit =
    {
        Projection: LifecycleProjection
        Change: LifecycleChange
        RequestEventId: Guid
        ReferenceCommitment: byte array
        SuppressionKeyId: Guid
        RequestCandidateCommitment: byte array
        ProposalCommitment: byte array
        WitnessSeal: WitnessDenialSeal
        CopySeal: ManagedCopyInventorySeal
        Approvals: ErasurePurgeApprovalReceipt list
        CanonicalAction: byte array
        Intent: WitnessIntent
        ObservedAt: DateTimeOffset
    }

/// Owner-only compare-and-update of the request tombstone, preserving a non-payload purge proof.
module internal CaseErasurePurgeWrite =
    let private invalid () =
        raise (InvalidDataException("Erasure purge proof is inconsistent."))

    let private ticketMatches (proof: ErasurePurgeCommit) =
        let ticket = proof.Intent.Ticket
        let seal = proof.WitnessSeal

        ticket.OperationId = proof.Change.EventId
        && ticket.Phase = Intent
        && ticket.ScopeKind = Case
        && ticket.SubjectCaseId = Some proof.Projection.CaseId
        && ticket.Sequence > seal.CutoffSequence

    let private copyMatches (proof: ErasurePurgeCommit) =
        let seal = proof.WitnessSeal
        let copy = proof.CopySeal

        copy.CaseId = proof.Projection.CaseId
        && copy.WitnessCutoffSequence = seal.CutoffSequence
        && copy.WitnessCutoffHash = seal.CutoffHash
        && copy.InventorySha256.Length = 32
        && copy.CopyCount >= 0L
        && copy.ObservedAt.Offset = TimeSpan.Zero
        && proof.ObservedAt.Offset = TimeSpan.Zero
        && (copy.ObservedAt - proof.ObservedAt).Duration() <= TimeSpan.FromMinutes 5.0

    let private commitmentsMatch (proof: ErasurePurgeCommit) =
        proof.ReferenceCommitment.Length = 32
        && proof.RequestCandidateCommitment.Length = 32
        && proof.ProposalCommitment.Length = 32
        && proof.Approvals.Length = 2
        && (proof.Approvals
            |> List.forall (fun approval ->
                approval.DraftCommitment.Length = 32 && approval.ApprovalCommitment.Length = 32))

    let private valid (proof: ErasurePurgeCommit) =
        if
            not (ticketMatches proof)
            || not (copyMatches proof)
            || not (commitmentsMatch proof)
            || proof.CanonicalAction.Length < 1
            || proof.CanonicalAction.Length > 16384
            || proof.Intent.CandidateHash <> SHA256.HashData(proof.CanonicalAction)
        then
            invalid ()

    let private updateSql =
        "UPDATE claimcore.case_erasure_tombstones SET "
        + "phase='ERASURE_PENDING',identity_coverage='WITNESS_COMPLETE',"
        + "purge_event_id=@event,purge_executor_kind='SCHEMA_OWNER_PROCESS',"
        + "request_candidate_sha256=NULL,request_candidate_commitment=@requestCommitment,"
        + "purge_proposal_commitment=@proposal,purge_valid_until=@validUntil,"
        + "purge_source_revision=@revision,purge_lifecycle_sequence=@sequence,"
        + "purge_lifecycle_hash=@lifecycleHash,purge_witness_cutoff_sequence=@cutoff,"
        + "purge_witness_cutoff_hash=@cutoffHash,"
        + "purge_subject_intent_count=@intentCount,"
        + "purge_subject_intent_sha256=@intentDigest,"
        + "purge_denial_count=@denialCount,purge_denial_set_sha256=@denialDigest,"
        + "purge_copy_inventory_sha256=@copyDigest,purge_canonical_action=@canonical,"
        + "purge_candidate_sha256=@candidate,purge_witness_sequence=@witnessSequence,"
        + "purge_witness_epoch=@witnessEpoch,purge_witness_entry_hash=@witnessHash,"
        + "live_purged_at=@instant WHERE case_id=@case AND phase='ERASURE_REQUESTED' "
        + "AND purge_event_id IS NULL AND request_event_id=@request "
        + "AND suppression_key_id=@key AND reference_commitment=@reference"

    let private bind (command: NpgsqlCommand) (proof: ErasurePurgeCommit) =
        let projection = proof.Projection
        let seal = proof.WitnessSeal
        let ticket = proof.Intent.Ticket

        let validUntil =
            match proof.Change.Action with
            | LifecycleMutation.PurgeLivePayload(_, value) -> value
            | _ -> invalid ()

        Sql.uuid command "event" proof.Change.EventId

        Sql.add
            command
            "requestCommitment"
            NpgsqlDbType.Bytea
            (box proof.RequestCandidateCommitment)

        Sql.add command "proposal" NpgsqlDbType.Bytea (box proof.ProposalCommitment)
        Sql.add command "validUntil" NpgsqlDbType.TimestampTz (box validUntil)
        Sql.integer command "revision" (Claim.view projection.Claim).Version
        Sql.integer command "sequence" projection.Sequence
        Sql.add command "lifecycleHash" NpgsqlDbType.Bytea (box projection.EventHash)
        Sql.integer command "cutoff" seal.CutoffSequence
        Sql.add command "cutoffHash" NpgsqlDbType.Bytea (box seal.CutoffHash)
        Sql.integer command "intentCount" seal.IntentCount
        Sql.add command "intentDigest" NpgsqlDbType.Bytea (box seal.IntentDigest)
        Sql.integer command "denialCount" seal.DenialCount
        Sql.add command "denialDigest" NpgsqlDbType.Bytea (box seal.DenialDigest)
        Sql.add command "copyDigest" NpgsqlDbType.Bytea (box proof.CopySeal.InventorySha256)
        Sql.add command "canonical" NpgsqlDbType.Bytea (box proof.CanonicalAction)
        Sql.add command "candidate" NpgsqlDbType.Bytea (box proof.Intent.CandidateHash)
        Sql.integer command "witnessSequence" ticket.Sequence
        Sql.integer command "witnessEpoch" ticket.Epoch
        Sql.add command "witnessHash" NpgsqlDbType.Bytea (box ticket.EntryHash)
        Sql.add command "instant" NpgsqlDbType.TimestampTz (box proof.ObservedAt)
        Sql.uuid command "case" projection.CaseId
        Sql.uuid command "request" proof.RequestEventId
        Sql.uuid command "key" proof.SuppressionKeyId
        Sql.add command "reference" NpgsqlDbType.Bytea (box proof.ReferenceCommitment)

    let private approvalSql =
        "INSERT INTO claimcore.case_erasure_purge_approvals "
        + "(approval_id,purge_event_id,case_id,approver_actor_id,approver_grant_revision,"
        + "approved_at,expires_at,draft_commitment,approval_commitment,"
        + "witness_sequence,witness_epoch,witness_entry_hash) VALUES "
        + "(@approval,@event,@case,@approver,@grant,@approved,@expires,@draft,@approvalCommitment,"
        + "@sequence,@epoch,@hash)"

    let private authorityTipSql =
        "INSERT INTO claimcore.case_erasure_authority_tip(case_id) VALUES (@case)"

    let private retainAuthorityTip
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (caseId: Guid)
        =
        task {
            use command = new NpgsqlCommand(authorityTipSql, connection, transaction)
            Sql.uuid command "case" caseId
            let! inserted = command.ExecuteNonQueryAsync()

            if inserted <> 1 then
                invalid ()
        }

    let private retainApproval
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (proof: ErasurePurgeCommit)
        (approval: ErasurePurgeApprovalReceipt)
        =
        task {
            use command = new NpgsqlCommand(approvalSql, connection, transaction)
            Sql.uuid command "approval" approval.ApprovalId
            Sql.uuid command "event" proof.Change.EventId
            Sql.uuid command "case" proof.Projection.CaseId
            Sql.uuid command "approver" approval.ApproverId
            Sql.integer command "grant" approval.GrantRevision
            Sql.add command "approved" NpgsqlDbType.TimestampTz (box approval.ApprovedAt)
            Sql.add command "expires" NpgsqlDbType.TimestampTz (box approval.ExpiresAt)
            Sql.add command "draft" NpgsqlDbType.Bytea (box approval.DraftCommitment)

            Sql.add
                command
                "approvalCommitment"
                NpgsqlDbType.Bytea
                (box approval.ApprovalCommitment)

            Sql.integer command "sequence" approval.WitnessSequence
            Sql.integer command "epoch" approval.WitnessEpoch
            Sql.add command "hash" NpgsqlDbType.Bytea (box approval.WitnessHash)

            let! inserted = command.ExecuteNonQueryAsync()

            if inserted <> 1 then
                invalid ()
        }

    let persist (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) proof =
        task {
            valid proof
            use command = new NpgsqlCommand(updateSql, connection, transaction)
            bind command proof
            let! updated = command.ExecuteNonQueryAsync()

            if updated <> 1 then
                invalid ()

            do! retainAuthorityTip connection transaction proof.Projection.CaseId

            for approval in proof.Approvals do
                do! retainApproval connection transaction proof approval
        }
