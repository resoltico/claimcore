namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application

/// An owner approval references one previously witnessed, current human key holder.
module internal ManagedCopySignerApprovalHolder =
    let private exact
        (request: CopySignerApprovalRequest)
        actorId
        (holder: SignerApprovalEvidence)
        =
        holder.ActorId <> actorId
        && holder.ActorRole <> "OWNER"
        && holder.HolderApprovalId.IsNone
        && holder.SigningKeyId = request.SigningKeyId
        && holder.Action = request.Action
        && holder.Purpose = request.Purpose
        && holder.PublicKeySha256 = request.PublicKeySha256
        && holder.ExpiresAt >= request.ExpiresAt
        && holder.CurrentActorAndGrant
        && not holder.Used
        && ManagedCopySignerApprovalRead.canonicalMatches holder

    let approved
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (request: CopySignerApprovalRequest)
        actorId
        ct
        =
        task {
            match request.Role with
            | CopySignerApprovalRole.Custodian -> return true
            | CopySignerApprovalRole.Owner holderId ->
                let! prior = ManagedCopySignerApprovalRead.load connection transaction holderId

                match prior with
                | Some holder when exact request actorId holder ->
                    do!
                        witness.VerifyAuthorityEvidence(
                            holder.ApprovalId,
                            holder.WitnessSequence,
                            holder.WitnessEpoch,
                            holder.WitnessEntryHash,
                            holder.CandidateSha256,
                            ct
                        )

                    return true
                | _ -> return false
        }

    let matchingSigner connection transaction (request: CopySignerApprovalRequest) actorId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT active,public_key_sha256,signer_purpose,holder_actor_id "
                    + "FROM claimcore.managed_copy_signers "
                    + "WHERE signing_key_id=@key",
                    connection,
                    transaction
                )

            Sql.uuid command "key" request.SigningKeyId
            let! rows = command.ExecuteReaderAsync()
            use reader = rows
            let! found = reader.ReadAsync()

            return
                match request.Action, found with
                | CopySignerAction.Register, false -> true
                | CopySignerAction.Retire, true ->
                    reader.GetBoolean(0)
                    && reader.GetString(2) = ManagedCopySignerCandidate.purposeName request.Purpose
                    && (match request.Role with
                        | CopySignerApprovalRole.Custodian -> reader.GetGuid(3) = actorId
                        | CopySignerApprovalRole.Owner _ -> true)
                    && CryptographicOperations.FixedTimeEquals(
                        ReadOnlySpan<byte>(reader.GetFieldValue<byte array>(1)),
                        ReadOnlySpan<byte>(request.PublicKeySha256)
                    )
                | _ -> false
        }
