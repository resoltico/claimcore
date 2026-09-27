namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Witness
open WitnessProtocolReconciliation

[<NoEquality; NoComparison>]
type internal StoredUseApproval =
    {
        Request: RealDataActivationApprovalRequest
        ActorId: Guid
        GrantRevision: int64
        ApprovedAt: DateTimeOffset
        Canonical: byte array
        CandidateHash: byte array
        IntentSequence: int64
        IntentEpoch: int64
        IntentHash: byte array
        CurrentHumanOwner: bool
        Human: bool
        Used: bool
    }

/// Two current human owners, two exact witnessed approvals, one consumed decision.
module internal InstallationUseActivationApprovals =
    let private query =
        "SELECT a.approval_id,a.plan_id,a.activation_id,a.installation_id,a.lineage_id,"
        + "a.witness_epoch,a.writer_generation,a.activation_plan_sha256,a.policy_sha256,"
        + "a.review_witness_sequence,a.review_witness_hash,a.expected_prior_sequence,"
        + "a.expected_prior_hash,a.approver_actor_id,a.approver_grant_revision,"
        + "a.approved_at,a.expires_at,a.canonical_action,a.candidate_sha256,"
        + "a.witness_sequence,a.approval_witness_epoch,a.witness_entry_hash,"
        + "(actor.principal_kind='HUMAN' AND actor.enabled AND EXISTS("
        + "SELECT 1 FROM claimcore.actor_grants g WHERE g.actor_id=a.approver_actor_id "
        + "AND g.active AND g.scope_kind='INSTALLATION' "
        + "AND g.scope_case_id='00000000-0000-0000-0000-000000000000'::uuid "
        + "AND g.role_name='OWNER')) AS current_owner,"
        + "EXISTS(SELECT 1 FROM claimcore.installation_data_use_approval_uses u "
        + "WHERE u.approval_id=a.approval_id) AS used,"
        + "actor.principal_kind='HUMAN' AS human "
        + "FROM claimcore.installation_data_use_approvals a "
        + "JOIN claimcore.actors actor ON actor.actor_id=a.approver_actor_id "
        + "WHERE a.approval_id IN (@first,@second) ORDER BY a.witness_sequence "
        + "FOR UPDATE OF a"

    let private readOnlyQuery = query.Replace(" FOR UPDATE OF a", "")

    let private read (reader: System.Data.Common.DbDataReader) =
        let request: RealDataActivationApprovalRequest =
            {
                ApprovalId = reader.GetGuid(0)
                PlanId = reader.GetGuid(1)
                ActivationId = reader.GetGuid(2)
                InstallationId = reader.GetGuid(3)
                LineageId = reader.GetGuid(4)
                Epoch = reader.GetInt64(5)
                WriterGeneration = reader.GetInt64(6)
                ActivationPlanSha256 = reader.GetFieldValue<byte array>(7)
                PolicySha256 = reader.GetFieldValue<byte array>(8)
                ReviewWitnessSequence = reader.GetInt64(9)
                ReviewWitnessHash = reader.GetFieldValue<byte array>(10)
                ExpectedWitnessSequence = reader.GetInt64(11)
                ExpectedWitnessHash = reader.GetFieldValue<byte array>(12)
                ExpiresAt = reader.GetFieldValue<DateTimeOffset>(16)
            }

        {
            Request = request
            ActorId = reader.GetGuid(13)
            GrantRevision = reader.GetInt64(14)
            ApprovedAt = reader.GetFieldValue<DateTimeOffset>(15)
            Canonical = reader.GetFieldValue<byte array>(17)
            CandidateHash = reader.GetFieldValue<byte array>(18)
            IntentSequence = reader.GetInt64(19)
            IntentEpoch = reader.GetInt64(20)
            IntentHash = reader.GetFieldValue<byte array>(21)
            CurrentHumanOwner = reader.GetBoolean(22)
            Used = reader.GetBoolean(23)
            Human = reader.GetBoolean(24)
        }

    let private rows connection transaction firstId secondId lockRows =
        use command =
            new NpgsqlCommand((if lockRows then query else readOnlyQuery), connection, transaction)

        Sql.uuid command "first" firstId
        Sql.uuid command "second" secondId
        use reader = command.ExecuteReader()
        let found = ResizeArray<StoredUseApproval>()

        while reader.Read() do
            found.Add(read reader)

        found |> Seq.toList

    let internal evidence (witness: WitnessProtocol) (row: StoredUseApproval) now historical =
        let request = row.Request

        let canonical =
            RealDataActivationApprovalCandidate.canonical
                request
                row.ActorId
                row.GrantRevision
                row.ApprovedAt

        try
            if
                row.Canonical <> canonical
                || row.CandidateHash <> SHA256.HashData(canonical)
                || row.IntentEpoch <> witness.Identity.Epoch
                || row.GrantRevision < 1L
                || not row.Human
                || request.ExpiresAt > row.ApprovedAt.AddHours(24.)
                || row.IntentSequence <> request.ExpectedWitnessSequence + 1L
                || (not historical
                    && (not row.CurrentHumanOwner
                        || row.Used
                        || row.ApprovedAt > now
                        || request.ExpiresAt <= now))
            then
                invalidOp "Human owner activation approval is invalid."

            witness.VerifyAuthorityEvidenceForInstallation(
                request.ApprovalId,
                row.IntentSequence,
                row.IntentEpoch,
                row.IntentHash,
                row.CandidateHash
            )

            let settled =
                witness.EvidenceStore.TryReadEvidence(request.ApprovalId, SettledAuthority)
                |> Option.defaultWith (fun () -> invalidOp "Owner approval did not settle.")

            if settled.Ticket.Sequence <> row.IntentSequence + 1L then
                invalidOp "Owner approval ticket chain diverged."

            {
                ApprovalId = request.ApprovalId
                ActorId = row.ActorId
                GrantRevision = row.GrantRevision
                IntentSequence = row.IntentSequence
                IntentHash = row.IntentHash
                SettlementSequence = settled.Ticket.Sequence
                SettlementHash = settled.Ticket.EntryHash
                ExpiresAt = request.ExpiresAt
            }
        finally
            CryptographicOperations.ZeroMemory(canonical)

    let verify
        connection
        transaction
        (witness: WitnessProtocol)
        (plan: PublishedInstallationUsePlan)
        firstId
        secondId
        now
        =
        if firstId = secondId then
            invalidOp "Distinct owner approvals are required."

        let found = rows connection transaction firstId secondId true

        match found with
        | [ first; second ] ->
            let a = evidence witness first now false
            let b = evidence witness second now false
            InstallationUseApprovalChain.verify first.Request second.Request a b plan witness

            {
                First = a
                Second = b
                ReviewSequence = first.Request.ReviewWitnessSequence
                ReviewHash = first.Request.ReviewWitnessHash
            }
        | _ -> invalidOp "Two witnessed human owner approvals are unavailable."

    let private sameEvidence
        (left: InstallationUseApprovalEvidence)
        (right: InstallationUseApprovalEvidence)
        =
        left.ApprovalId = right.ApprovalId
        && left.ActorId = right.ActorId
        && left.GrantRevision = right.GrantRevision
        && left.IntentSequence = right.IntentSequence
        && left.IntentHash = right.IntentHash
        && left.SettlementSequence = right.SettlementSequence
        && left.SettlementHash = right.SettlementHash
        && left.ExpiresAt = right.ExpiresAt

    let verifyHistorical
        connection
        transaction
        (witness: WitnessProtocol)
        (plan: PublishedInstallationUsePlan)
        (record: InstallationUseActivationRecord)
        =
        let found =
            rows
                connection
                transaction
                record.Approvals.First.ApprovalId
                record.Approvals.Second.ApprovalId
                false

        match found with
        | [ first; second ] ->
            let a = evidence witness first record.HealthCheckedAt true
            let b = evidence witness second record.HealthCheckedAt true
            InstallationUseApprovalChain.verify first.Request second.Request a b plan witness

            if
                record.Approvals.ReviewSequence <> first.Request.ReviewWitnessSequence
                || record.Approvals.ReviewHash <> first.Request.ReviewWitnessHash
                || not (sameEvidence a record.Approvals.First)
                || not (sameEvidence b record.Approvals.Second)
            then
                invalidOp "Historical owner approvals diverged from activation."

            {
                First = a
                Second = b
                ReviewSequence = first.Request.ReviewWitnessSequence
                ReviewHash = first.Request.ReviewWitnessHash
            }
        | _ -> invalidOp "Historical owner approvals are missing."

    let verifyOneReadOnly connection transaction (witness: WitnessProtocol) approvalId cutoff ct =
        task {
            match rows connection transaction approvalId approvalId false with
            | [ row ] when row.IntentSequence <= cutoff ->
                evidence witness row DateTimeOffset.MinValue true |> ignore

                do!
                    DataAuditHandoffOwnerRole.verify
                        connection
                        transaction
                        row.ActorId
                        row.GrantRevision
                        ct
            | _ -> invalidOp "Witnessed activation approval is missing or beyond audit cutoff."
        }

    let consume
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        activationId
        (approvals: InstallationUseApprovalPair)
        =
        for slot, approval in [ 1, approvals.First; 2, approvals.Second ] do
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.installation_data_use_approval_uses "
                    + "(approval_id,activation_id,slot) VALUES (@approval,@activation,@slot)",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approval.ApprovalId
            Sql.uuid command "activation" activationId
            Sql.add command "slot" NpgsqlDbType.Integer (box slot)

            if command.ExecuteNonQuery() <> 1 then
                invalidOp "Owner activation approval was not consumed exactly once."
