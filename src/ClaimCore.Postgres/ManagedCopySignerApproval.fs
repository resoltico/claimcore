namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

/// Authenticated service approval only. Schema ownership and public keys stay out of this path.
module internal ManagedCopySignerApproval =
    let private roleName (request: CopySignerApprovalRequest) (authority: ActorAuthority) =
        let contains (role: Role) =
            authority.Grants
            |> List.exists (fun grant -> grant.Scope = GrantScope.Installation && grant.Role = role)

        match request.Role with
        | CopySignerApprovalRole.Owner _ when contains Role.Owner -> Some "OWNER"
        | CopySignerApprovalRole.Custodian when contains Role.AuditorCustodian ->
            Some "AUDITOR_CUSTODIAN"
        | CopySignerApprovalRole.Custodian when contains Role.DataSteward -> Some "DATA_STEWARD"
        | _ -> None

    let private existing connection transaction approvalId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT canonical_action,candidate_sha256,witness_sequence,witness_epoch,"
                    + "witness_entry_hash FROM claimcore.managed_copy_signer_approvals "
                    + "WHERE approval_id=@approval",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            let! rows = command.ExecuteReaderAsync()
            use reader = rows
            let! found = reader.ReadAsync()

            return
                if found then
                    Some(
                        reader.GetFieldValue<byte array>(0),
                        reader.GetFieldValue<byte array>(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetFieldValue<byte array>(4)
                    )
                else
                    None
        }

    let private append connection transaction request actorId actorRole revision canonical intent =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.managed_copy_signer_approvals "
                    + "(approval_id,signing_key_id,action_name,signer_purpose,holder_approval_id,public_key_sha256,actor_id,"
                    + "actor_role,grant_revision,expires_at,canonical_action,candidate_sha256,"
                    + "witness_sequence,witness_epoch,witness_entry_hash) "
                    + "VALUES (@approval,@key,@action,@purpose,@holderApproval,@publicHash,@actor,@role,@revision,"
                    + "@expires,@canonical,@candidate,@sequence,@epoch,@entryHash)",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" request.ApprovalId
            Sql.uuid command "key" request.SigningKeyId
            Sql.text command "action" (ManagedCopySignerCandidate.actionName request.Action)
            Sql.text command "purpose" (ManagedCopySignerCandidate.purposeName request.Purpose)

            Sql.optional
                command
                "holderApproval"
                NpgsqlDbType.Uuid
                (match request.Role with
                 | CopySignerApprovalRole.Owner id -> Some id
                 | CopySignerApprovalRole.Custodian -> None)

            Sql.add command "publicHash" NpgsqlDbType.Bytea (box request.PublicKeySha256)
            Sql.uuid command "actor" actorId
            Sql.text command "role" actorRole
            Sql.integer command "revision" revision
            Sql.add command "expires" NpgsqlDbType.TimestampTz (box request.ExpiresAt)
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.integer command "epoch" intent.Ticket.Epoch
            Sql.add command "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            let! count = command.ExecuteNonQueryAsync()

            if count <> 1 then
                invalidOp "Signer approval evidence was not retained."
        }

    let private validRequest (context: ActorCallContext) (request: CopySignerApprovalRequest) =
        request.ApprovalId <> Guid.Empty
        && request.SigningKeyId <> Guid.Empty
        && not (obj.ReferenceEquals(request.PublicKeySha256, null))
        && request.PublicKeySha256.Length = 32
        && Sql.isUtcMicrosecond request.ExpiresAt
        && context.Action = EndpointAction.ApproveCopySigner
        && context.CaseId.IsNone
        && PrincipalKey.isHuman context.Binding.Principal
        && (match request.Role with
            | CopySignerApprovalRole.Owner holder ->
                holder <> Guid.Empty && holder <> request.ApprovalId
            | CopySignerApprovalRole.Custodian -> true)

    let private authorizedRole
        context
        request
        revision
        (live: ActorAuthority)
        (now: DateTimeOffset)
        =
        let authorized =
            match
                ActorAuthorization.authorizeAtRevision
                    context.Binding.Principal
                    live
                    context.Binding.GrantRevision
                    EndpointAction.ApproveCopySigner
                    ResourceScope.Installation
            with
            | AuthorizationDecision.Available(actorId, _) -> actorId = context.Binding.ActorId
            | AuthorizationDecision.Unavailable -> false

        if
            not authorized
            || request.ExpiresAt < now.AddMinutes(1.0)
            || request.ExpiresAt > now.AddHours(1.0)
            || revision <> live.GrantRevision
        then
            None
        else
            roleName request live

    let private approveFresh
        connection
        transaction
        (witness: WitnessProtocol)
        (request: CopySignerApprovalRequest)
        actorId
        actorRole
        revision
        canonical
        ct
        =
        task {
            let! holder =
                ManagedCopySignerApprovalHolder.approved
                    connection
                    transaction
                    witness
                    request
                    actorId
                    ct

            let! target =
                ManagedCopySignerApprovalHolder.matchingSigner
                    connection
                    transaction
                    request
                    actorId

            if not target || not holder then
                return CopySignerApprovalOutcome.ResourceUnavailable
            else
                let! intent = witness.BeginAuthority(request.ApprovalId, canonical, None, ct)

                do!
                    append
                        connection
                        transaction
                        request
                        actorId
                        actorRole
                        revision
                        canonical
                        intent

                do! transaction.CommitAsync()
                let! _ = witness.SettleAuthority(request.ApprovalId, intent)
                return CopySignerApprovalOutcome.Approved(request.ApprovalId, revision)
        }

    let private persistOrReplay
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (request: CopySignerApprovalRequest)
        (actorId: Guid)
        (actorRole: string)
        (revision: int64)
        (canonical: byte array)
        ct
        =
        task {
            let! prior = existing connection transaction request.ApprovalId

            match prior with
            | Some(bytes, digest, sequence, epoch, entryHash) when bytes = canonical ->
                do!
                    witness.VerifyAuthorityEvidence(
                        request.ApprovalId,
                        sequence,
                        epoch,
                        entryHash,
                        digest,
                        ct
                    )

                return CopySignerApprovalOutcome.Approved(request.ApprovalId, revision)
            | Some _ -> return CopySignerApprovalOutcome.ResourceUnavailable
            | None ->
                return!
                    approveFresh
                        connection
                        transaction
                        witness
                        request
                        actorId
                        actorRole
                        revision
                        canonical
                        ct
        }

    let private underLock connection transaction witness context request ct =
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
            | None -> return CopySignerApprovalOutcome.ResourceUnavailable
            | Some live ->
                let! now = Sql.databaseNow connection transaction ct

                match authorizedRole context request revision live now with
                | None -> return CopySignerApprovalOutcome.ResourceUnavailable
                | Some actorRole ->
                    let canonical =
                        ManagedCopySignerCandidate.approval request live.ActorId actorRole revision

                    try
                        return!
                            persistOrReplay
                                connection
                                transaction
                                witness
                                request
                                live.ActorId
                                actorRole
                                revision
                                canonical
                                ct
                    finally
                        CryptographicOperations.ZeroMemory(canonical)
        }

    let approve
        (dataSource: NpgsqlDataSource)
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (request: CopySignerApprovalRequest)
        ct
        =
        task {
            if not (validRequest context request) then
                return CopySignerApprovalOutcome.ResourceUnavailable
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
                    return CopySignerApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }
