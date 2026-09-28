namespace ClaimCore.Postgres

open System
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
        =
        task {
            match request.Role with
            | CopySignerApprovalRole.Custodian -> return true
            | CopySignerApprovalRole.Owner holderId ->
                let! prior = ManagedCopySignerApprovalRead.load connection transaction holderId

                match prior with
                | Some holder when exact request actorId holder ->
                    witness.VerifyAuthorityEvidence(
                        holder.ApprovalId,
                        holder.WitnessSequence,
                        holder.WitnessEpoch,
                        holder.WitnessEntryHash,
                        holder.CandidateSha256
                    )

                    return true
                | _ -> return false
        }
