namespace ClaimCore.Postgres

open System
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open WitnessProtocolReconciliation

[<NoEquality; NoComparison>]
type internal RealDataActivationApprovalRow =
    {
        Canonical: byte array
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
        ApproverActorId: Guid
        ApproverGrantRevision: int64
        ApprovedAt: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal FirstRealDataActivationApproval =
    {
        ApprovalId: Guid
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
        CandidateHash: byte array
        ApproverActorId: Guid
        ExpiresAt: DateTimeOffset
        ExpectedSequence: int64
        ExpectedHash: byte array
        PlanSha256: byte array
        ReviewSequence: int64
        ReviewHash: byte array
    }

module internal RealDataActivationApprovalRows =
    let private insertSql =
        "INSERT INTO claimcore.installation_data_use_approvals "
        + "(approval_id,plan_id,activation_id,installation_id,lineage_id,witness_epoch,"
        + "writer_generation,activation_plan_sha256,policy_sha256,"
        + "review_witness_sequence,review_witness_hash,expected_prior_sequence,"
        + "expected_prior_hash,approver_actor_id,approver_grant_revision,"
        + "approved_at,expires_at,canonical_action,candidate_sha256,"
        + "witness_sequence,approval_witness_epoch,witness_entry_hash) VALUES "
        + "(@approval,@planId,@activation,@installation,@lineage,@epoch,@generation,"
        + "@plan,@policy,@reviewSequence,@reviewHash,@priorSequence,@priorHash,"
        + "@actor,@revision,@approvedAt,@expires,@canonical,@candidate,"
        + "@witnessSequence,@witnessEpoch,@entryHash)"

    let prior (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) approvalId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT canonical_action,candidate_sha256,witness_sequence,"
                    + "approval_witness_epoch,witness_entry_hash,approver_actor_id,"
                    + "approver_grant_revision,approved_at "
                    + "FROM claimcore.installation_data_use_approvals WHERE approval_id=@approval",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            use! reader = command.ExecuteReaderAsync()

            return
                if reader.Read() then
                    Some
                        {
                            Canonical = reader.GetFieldValue<byte array>(0)
                            CandidateHash = reader.GetFieldValue<byte array>(1)
                            WitnessSequence = reader.GetInt64(2)
                            WitnessEpoch = reader.GetInt64(3)
                            WitnessHash = reader.GetFieldValue<byte array>(4)
                            ApproverActorId = reader.GetGuid(5)
                            ApproverGrantRevision = reader.GetInt64(6)
                            ApprovedAt = reader.GetFieldValue<DateTimeOffset>(7)
                        }
                else
                    None
        }

    let first
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (request: RealDataActivationApprovalRequest)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT approval_id,witness_sequence,approval_witness_epoch,"
                    + "witness_entry_hash,candidate_sha256,approver_actor_id,expires_at,"
                    + "expected_prior_sequence,expected_prior_hash,activation_plan_sha256,"
                    + "review_witness_sequence,review_witness_hash "
                    + "FROM claimcore.installation_data_use_approvals "
                    + "WHERE activation_id=@activation AND approval_id<>@approval "
                    + "ORDER BY witness_sequence",
                    connection,
                    transaction
                )

            Sql.uuid command "activation" request.ActivationId
            Sql.uuid command "approval" request.ApprovalId
            use! reader = command.ExecuteReaderAsync()

            if reader.Read() then
                let first =
                    {
                        ApprovalId = reader.GetGuid(0)
                        WitnessSequence = reader.GetInt64(1)
                        WitnessEpoch = reader.GetInt64(2)
                        WitnessHash = reader.GetFieldValue<byte array>(3)
                        CandidateHash = reader.GetFieldValue<byte array>(4)
                        ApproverActorId = reader.GetGuid(5)
                        ExpiresAt = reader.GetFieldValue<DateTimeOffset>(6)
                        ExpectedSequence = reader.GetInt64(7)
                        ExpectedHash = reader.GetFieldValue<byte array>(8)
                        PlanSha256 = reader.GetFieldValue<byte array>(9)
                        ReviewSequence = reader.GetInt64(10)
                        ReviewHash = reader.GetFieldValue<byte array>(11)
                    }

                return if reader.Read() then None else Some first
            else
                return None
        }

    let insert
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (request: RealDataActivationApprovalRequest)
        actorId
        revision
        approvedAt
        canonical
        (intent: WitnessIntent)
        =
        task {
            use command = new NpgsqlCommand(insertSql, connection, transaction)

            Sql.uuid command "approval" request.ApprovalId
            Sql.uuid command "planId" request.PlanId
            Sql.uuid command "activation" request.ActivationId
            Sql.uuid command "installation" request.InstallationId
            Sql.uuid command "lineage" request.LineageId
            Sql.integer command "epoch" request.Epoch
            Sql.integer command "generation" request.WriterGeneration
            Sql.add command "plan" NpgsqlDbType.Bytea (box request.ActivationPlanSha256)
            Sql.add command "policy" NpgsqlDbType.Bytea (box request.PolicySha256)
            Sql.integer command "reviewSequence" request.ReviewWitnessSequence
            Sql.add command "reviewHash" NpgsqlDbType.Bytea (box request.ReviewWitnessHash)
            Sql.integer command "priorSequence" request.ExpectedWitnessSequence
            Sql.add command "priorHash" NpgsqlDbType.Bytea (box request.ExpectedWitnessHash)
            Sql.uuid command "actor" actorId
            Sql.integer command "revision" revision
            Sql.add command "approvedAt" NpgsqlDbType.TimestampTz (box approvedAt)
            Sql.add command "expires" NpgsqlDbType.TimestampTz (box request.ExpiresAt)
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.integer command "witnessSequence" intent.Ticket.Sequence
            Sql.integer command "witnessEpoch" intent.Ticket.Epoch
            Sql.add command "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            let! inserted = command.ExecuteNonQueryAsync()

            if inserted <> 1 then
                invalidOp "Real-data activation approval was not retained."
        }
