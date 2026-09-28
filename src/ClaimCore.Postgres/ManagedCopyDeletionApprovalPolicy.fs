namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open WitnessProtocolReconciliation

[<NoEquality; NoComparison>]
type internal CopyDeletionTarget =
    {
        SourceCaseId: Guid option
        Revision: int64
        State: string
        LocationCommitment: byte array
        RetainUntil: DateTimeOffset
        CopyHolderActorId: Guid
        VerifierHolderActorId: Guid
        VerifierActive: bool
        VerifierPurpose: CopySignerPurpose
    }

/// Actor-bound, witnessed approval of one exact copy/report. It cannot itself delete bytes.
module internal ManagedCopyDeletionApprovalPolicy =
    let validRequest (context: ActorCallContext) (request: CopyDeletionApprovalRequest) =
        request.ApprovalId <> Guid.Empty
        && request.DeletionEventId <> Guid.Empty
        && request.CopyId <> Guid.Empty
        && request.VerifierSigningKeyId <> Guid.Empty
        && request.ExpectedCopyRevision > 0L
        && not (isNull (box request.LocationCommitment))
        && request.LocationCommitment.Length = 32
        && not (isNull (box request.InspectionReportSha256))
        && request.InspectionReportSha256.Length = 32
        && request.WitnessCutoffSequence >= 0L
        && not (isNull (box request.WitnessCutoffHash))
        && request.WitnessCutoffHash.Length = 32
        && Sql.isUtcMicrosecond request.ExpiresAt
        && context.Action = EndpointAction.ApproveCopyDeletion
        && context.CaseId.IsNone
        && PrincipalKey.isHuman context.Binding.Principal

    let target (connection: NpgsqlConnection) transaction (request: CopyDeletionApprovalRequest) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT c.source_case_id,c.revision,c.state,c.location_commitment,c.retain_until,"
                    + "copy_holder.holder_actor_id,verifier.holder_actor_id,verifier.active,"
                    + "verifier.signer_purpose FROM claimcore.managed_copies c "
                    + "JOIN claimcore.managed_copy_signers copy_holder ON copy_holder.signing_key_id=c.signing_key_id "
                    + "JOIN claimcore.managed_copy_signers verifier ON verifier.signing_key_id=@verifier "
                    + "WHERE c.copy_id=@copy AND c.producer_kind='OWNER_ATTESTED'",
                    connection,
                    transaction
                )

            Sql.uuid command "copy" request.CopyId
            Sql.uuid command "verifier" request.VerifierSigningKeyId
            use! reader = command.ExecuteReaderAsync()

            return
                if reader.Read() then
                    Some
                        {
                            SourceCaseId =
                                if reader.IsDBNull(0) then None else Some(reader.GetGuid(0))
                            Revision = reader.GetInt64(1)
                            State = reader.GetString(2)
                            LocationCommitment = reader.GetFieldValue<byte array>(3)
                            RetainUntil = reader.GetFieldValue<DateTimeOffset>(4)
                            CopyHolderActorId = reader.GetGuid(5)
                            VerifierHolderActorId = reader.GetGuid(6)
                            VerifierActive = reader.GetBoolean(7)
                            VerifierPurpose =
                                ManagedCopySignerCandidate.purposeOfName (reader.GetString(8))
                        }
                else
                    None
        }

    let activeHold (connection: NpgsqlConnection) transaction (sourceCaseId: Guid option) =
        task {
            let predicate = if sourceCaseId.IsSome then "case_id=@case" else "TRUE"

            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM claimcore.case_holds WHERE released_at IS NULL AND "
                    + predicate
                    + ") OR EXISTS(SELECT 1 FROM claimcore.case_erasure_holds h "
                    + "LEFT JOIN claimcore.case_erasure_hold_releases r ON r.hold_id=h.hold_id "
                    + "WHERE r.hold_id IS NULL AND "
                    + (if sourceCaseId.IsSome then "h.case_id=@case" else "TRUE")
                    + ")",
                    connection,
                    transaction
                )

            sourceCaseId |> Option.iter (Sql.uuid command "case")
            let! result = command.ExecuteScalarAsync()
            return unbox<bool> result
        }

    let existing (connection: NpgsqlConnection) transaction approvalId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT canonical_action,candidate_sha256,witness_sequence,witness_epoch,"
                    + "witness_entry_hash FROM claimcore.managed_copy_deletion_approvals "
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
                        reader.GetFieldValue<byte array>(4)
                    )
                else
                    None
        }

    let private insert
        connection
        transaction
        (request: CopyDeletionApprovalRequest)
        actorId
        revision
        canonical
        (intent: WitnessIntent)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.managed_copy_deletion_approvals "
                    + "(approval_id,deletion_event_id,copy_id,verifier_signing_key_id,expected_copy_revision,"
                    + "location_commitment,inspection_report_sha256,witness_cutoff_sequence,"
                    + "witness_cutoff_hash,approver_actor_id,approver_grant_revision,expires_at,"
                    + "canonical_action,candidate_sha256,witness_sequence,witness_epoch,witness_entry_hash) "
                    + "VALUES (@approval,@event,@copy,@verifier,@revision,@location,@report,@cutoff,@cutoffHash,"
                    + "@actor,@grant,@expires,@canonical,@candidate,@sequence,@epoch,@entryHash)",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" request.ApprovalId
            Sql.uuid command "event" request.DeletionEventId
            Sql.uuid command "copy" request.CopyId
            Sql.uuid command "verifier" request.VerifierSigningKeyId
            Sql.integer command "revision" request.ExpectedCopyRevision
            Sql.add command "location" NpgsqlDbType.Bytea (box request.LocationCommitment)
            Sql.add command "report" NpgsqlDbType.Bytea (box request.InspectionReportSha256)
            Sql.integer command "cutoff" request.WitnessCutoffSequence
            Sql.add command "cutoffHash" NpgsqlDbType.Bytea (box request.WitnessCutoffHash)
            Sql.uuid command "actor" actorId
            Sql.integer command "grant" revision
            Sql.add command "expires" NpgsqlDbType.TimestampTz (box request.ExpiresAt)
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.integer command "epoch" intent.Ticket.Epoch
            Sql.add command "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            let! count = command.ExecuteNonQueryAsync()

            if count <> 1 then
                invalidOp "Copy deletion approval was not retained."
        }

    let authorized (context: ActorCallContext) (live: ActorAuthority) revision =
        let grant =
            ActorAuthorization.authorizeAtRevision
                context.Binding.Principal
                live
                context.Binding.GrantRevision
                EndpointAction.ApproveCopyDeletion
                ResourceScope.Installation

        let role =
            live.Grants
            |> List.exists (fun value ->
                value.Scope = GrantScope.Installation
                && (value.Role = Role.AuditorCustodian || value.Role = Role.DataSteward))

        match grant with
        | AuthorizationDecision.Available(actorId, _) ->
            actorId = context.Binding.ActorId && revision = live.GrantRevision && role
        | AuthorizationDecision.Unavailable -> false

    let admissible
        (request: CopyDeletionApprovalRequest)
        (copy: CopyDeletionTarget)
        actorId
        now
        held
        =
        copy.State = "DELETE_PENDING"
        && copy.Revision = request.ExpectedCopyRevision
        && copy.LocationCommitment = request.LocationCommitment
        && copy.RetainUntil <= now
        && copy.VerifierActive
        && copy.VerifierPurpose = CopySignerPurpose.DeletionVerifier
        && copy.VerifierHolderActorId = actorId
        && copy.CopyHolderActorId <> actorId
        && not held
        && request.ExpiresAt > now.AddMinutes(1.)
        && request.ExpiresAt <= now.AddHours(1.)

    let apply
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (request: CopyDeletionApprovalRequest)
        actorId
        revision
        (copy: CopyDeletionTarget)
        canonical
        =
        task {
            let intent =
                witness.BeginAuthority(request.ApprovalId, canonical, copy.SourceCaseId)

            do! insert connection transaction request actorId revision canonical intent
            do! transaction.CommitAsync()
            witness.SettleAuthority(request.ApprovalId, intent) |> ignore
            return CopyDeletionApprovalOutcome.Approved(request.ApprovalId, revision)
        }
