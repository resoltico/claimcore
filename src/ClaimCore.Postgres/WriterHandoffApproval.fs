namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open WitnessProtocolReconciliation

/// Authenticated owner approval is witnessed before an owner process may prepare a handoff.
module internal WriterHandoffApproval =
    let private currentOwner context (live: ActorAuthority) revision =
        let grant =
            ActorAuthorization.authorizeAtRevision
                context.Binding.Principal
                live
                context.Binding.GrantRevision
                EndpointAction.ApproveWriterHandoff
                ResourceScope.Installation

        let owner =
            live.Grants
            |> List.exists (fun value ->
                value.Scope = GrantScope.Installation && value.Role = Role.Owner)

        match grant with
        | AuthorizationDecision.Available(actorId, _) ->
            actorId = context.Binding.ActorId && revision = live.GrantRevision && owner
        | AuthorizationDecision.Unavailable -> false

    let private checkpointHolder
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        keyId
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT s.holder_actor_id "
                    + "FROM claimcore.managed_copy_signers s "
                    + "JOIN claimcore.actors a ON a.actor_id=s.holder_actor_id "
                    + "WHERE s.signing_key_id=@key AND s.active "
                    + "AND s.signer_purpose='CHECKPOINT' "
                    + "AND a.principal_kind='HUMAN' AND a.enabled "
                    + "AND EXISTS (SELECT 1 FROM claimcore.actor_grants g "
                    + "WHERE g.actor_id=s.holder_actor_id AND g.scope_kind='INSTALLATION' "
                    + "AND g.scope_case_id='00000000-0000-0000-0000-000000000000'::uuid "
                    + "AND g.active AND g.role_name IN ('AUDITOR_CUSTODIAN','DATA_STEWARD'))",
                    connection,
                    transaction
                )

            Sql.uuid command "key" keyId
            use! reader = command.ExecuteReaderAsync()

            return
                if reader.Read() then
                    let holder = reader.GetGuid(0)
                    if reader.Read() then None else Some holder
                else
                    None
        }

    let private prior connection transaction approvalId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT canonical_action,candidate_sha256,witness_sequence,witness_epoch,"
                    + "witness_entry_hash,approver_actor_id,approver_grant_revision "
                    + "FROM claimcore.writer_handoff_approvals "
                    + "WHERE approval_id=@approval",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            use! reader = command.ExecuteReaderAsync()

            return
                if reader.Read() then
                    Some(
                        reader.GetFieldValue<byte array>(0),
                        reader.GetFieldValue<byte array>(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetFieldValue<byte array>(4),
                        reader.GetGuid(5),
                        reader.GetInt64(6)
                    )
                else
                    None
        }

    let private replay (witness: WitnessProtocol) approvalId (canonical: byte array) stored ct =
        task {
            match stored with
            | Some(bytes, digest, sequence, epoch, hash, _, originalRevision) when
                bytes = canonical && digest = SHA256.HashData(canonical)
                ->
                do!
                    witness.VerifyAuthorityEvidenceForInstallation(
                        approvalId,
                        sequence,
                        epoch,
                        hash,
                        digest,
                        ct
                    )

                return WriterHandoffApprovalOutcome.Approved(approvalId, originalRevision)
            | _ -> return WriterHandoffApprovalOutcome.ResourceUnavailable
        }

    let private insert
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (request: WriterHandoffApprovalRequest)
        actorId
        revision
        canonical
        (intent: WitnessIntent)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.writer_handoff_approvals "
                    + "(approval_id,handoff_id,old_generation,expected_witness_sequence,"
                    + "expected_witness_hash,new_capability_sha256,checkpoint_signing_key_id,"
                    + "fence_report_sha256,inventory_sha256,approver_actor_id,"
                    + "approver_grant_revision,expires_at,canonical_action,candidate_sha256,"
                    + "witness_sequence,witness_epoch,witness_entry_hash) VALUES "
                    + "(@approval,@handoff,@generation,@sequence,@hash,@newCapability,@key,"
                    + "@fence,@inventory,@actor,@revision,@expires,@canonical,@candidate,"
                    + "@witnessSequence,@epoch,@entryHash)",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" request.ApprovalId
            Sql.uuid command "handoff" request.HandoffId
            Sql.integer command "generation" request.OldGeneration
            Sql.integer command "sequence" request.ExpectedWitnessSequence
            Sql.add command "hash" NpgsqlDbType.Bytea (box request.ExpectedWitnessHash)
            Sql.add command "newCapability" NpgsqlDbType.Bytea (box request.NewCapabilitySha256)
            Sql.uuid command "key" request.CheckpointSigningKeyId
            Sql.add command "fence" NpgsqlDbType.Bytea (box request.FenceReportSha256)
            Sql.add command "inventory" NpgsqlDbType.Bytea (box request.InventorySha256)
            Sql.uuid command "actor" actorId
            Sql.integer command "revision" revision
            Sql.add command "expires" NpgsqlDbType.TimestampTz (box request.ExpiresAt)
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.integer command "witnessSequence" intent.Ticket.Sequence
            Sql.integer command "epoch" intent.Ticket.Epoch
            Sql.add command "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            let! inserted = command.ExecuteNonQueryAsync()

            if inserted <> 1 then
                invalidOp "Writer handoff approval was not retained."
        }

    let private fresh
        connection
        transaction
        (witness: WitnessProtocol)
        (request: WriterHandoffApprovalRequest)
        actorId
        revision
        canonical
        ct
        =
        task {
            let! holder = checkpointHolder connection transaction request.CheckpointSigningKeyId
            let! now = Sql.databaseNow connection transaction ct
            let! snapshot = witness.Snapshot(ct)

            let! historical =
                task {
                    try
                        do!
                            witness.VerifyHistoricalTip(
                                request.ExpectedWitnessSequence,
                                request.ExpectedWitnessHash,
                                ct
                            )

                        return true
                    with _ ->
                        return false
                }

            match holder with
            | None -> return WriterHandoffApprovalOutcome.ResourceUnavailable
            | Some holderId when holderId = actorId ->
                return WriterHandoffApprovalOutcome.ResourceUnavailable
            | Some _ when
                not historical
                || snapshot.HandoffPending
                || snapshot.WriterGeneration <> request.OldGeneration
                || request.ExpiresAt <= now.AddMinutes(1.)
                || request.ExpiresAt > now.AddHours(1.)
                ->
                return WriterHandoffApprovalOutcome.ResourceUnavailable
            | Some _ ->
                let! intent = witness.BeginAuthority(request.ApprovalId, canonical, None, ct)
                do! insert connection transaction request actorId revision canonical intent
                do! transaction.CommitAsync()
                let! _ = witness.SettleAuthority(request.ApprovalId, intent)
                return WriterHandoffApprovalOutcome.Approved(request.ApprovalId, revision)
        }

    let private underLock
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (request: WriterHandoffApprovalRequest)
        ct
        =
        task {
            let! revision =
                ActorGrantRead.lockRevision connection transaction true CancellationToken.None

            let! authority =
                ActorGrantRead.loadUnderLock
                    connection
                    transaction
                    context.Binding.Principal
                    ResourceScope.Installation
                    revision
                    CancellationToken.None

            match authority with
            | Some live when currentOwner context live revision ->
                let! stored = prior connection transaction request.ApprovalId

                let originalRevision =
                    match stored with
                    | Some(_, _, _, _, _, actorId, approvedRevision) when actorId = live.ActorId ->
                        approvedRevision
                    | Some _ -> 0L
                    | None -> revision

                let canonical =
                    WriterHandoffApprovalCandidate.canonical request live.ActorId originalRevision

                try
                    match stored with
                    | Some _ when originalRevision > 0L ->
                        return! replay witness request.ApprovalId canonical stored ct
                    | Some _ -> return WriterHandoffApprovalOutcome.ResourceUnavailable
                    | None ->
                        return!
                            fresh
                                connection
                                transaction
                                witness
                                request
                                live.ActorId
                                revision
                                canonical
                                ct
                finally
                    CryptographicOperations.ZeroMemory(canonical)
            | _ -> return WriterHandoffApprovalOutcome.ResourceUnavailable
        }

    let approve dataSource (witness: WitnessProtocol) context request ct =
        task {
            if not (WriterHandoffApprovalCandidate.valid context request) then
                return WriterHandoffApprovalOutcome.ResourceUnavailable
            else
                try
                    do! witness.Admit(ct)

                    use! connection =
                        RuntimeDatabase.openConnectionAsyncWithCancellation dataSource ct

                    use! _authorityLease =
                        AuthorityOperationFence.acquireShared (Some dataSource) connection ct

                    use! transaction =
                        connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

                    return! underLock connection transaction witness context request ct
                with _ ->
                    return WriterHandoffApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }
