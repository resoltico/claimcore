namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application

module internal ManagedCopySignerPolicy =
    let matchesInstallation connection transaction (witness: WitnessProtocol) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT installation_id,lineage_id,witness_epoch "
                    + "FROM claimcore.installation_lineage WHERE singleton",
                    connection,
                    transaction
                )

            let! rows = command.ExecuteReaderAsync()
            use reader = rows
            let! found = reader.ReadAsync()
            let identity = witness.Identity

            return
                found
                && reader.GetGuid(0) = identity.InstallationId
                && reader.GetGuid(1) = identity.LineageId
                && reader.GetInt64(2) = identity.Epoch
                && not (reader.Read())
        }

    let private exactApproval
        now
        keyId
        action
        purpose
        (publicHash: byte array)
        (value: SignerApprovalEvidence)
        =
        value.SigningKeyId = keyId
        && value.Action = action
        && value.Purpose = purpose
        && value.PublicKeySha256 = publicHash
        && value.ExpiresAt > now
        && value.CurrentActorAndGrant
        && not value.Used
        && ManagedCopySignerApprovalRead.canonicalMatches value

    let private ordered (first: SignerApprovalEvidence) (second: SignerApprovalEvidence) =
        match first.ActorRole, second.ActorRole with
        | "OWNER", ("AUDITOR_CUSTODIAN" | "DATA_STEWARD") -> Some(first, second)
        | ("AUDITOR_CUSTODIAN" | "DATA_STEWARD"), "OWNER" -> Some(second, first)
        | _ -> None

    let approved
        (witness: WitnessProtocol)
        now
        keyId
        action
        purpose
        publicHash
        (first: SignerApprovalEvidence)
        (second: SignerApprovalEvidence)
        =
        task {
            if
                first.ApprovalId = second.ApprovalId
                || first.ActorId = second.ActorId
                || not (
                    exactApproval now keyId action purpose publicHash first
                    && exactApproval now keyId action purpose publicHash second
                )
            then
                return None
            else
                match ordered first second with
                | None -> return None
                | Some(owner, custodian) ->
                    if
                        owner.HolderApprovalId <> Some custodian.ApprovalId
                        || custodian.HolderApprovalId.IsSome
                    then
                        return None
                    else
                        do!
                            witness.VerifyAuthorityEvidence(
                                owner.ApprovalId,
                                owner.WitnessSequence,
                                owner.WitnessEpoch,
                                owner.WitnessEntryHash,
                                owner.CandidateSha256,
                                CancellationToken.None
                            )

                        do!
                            witness.VerifyAuthorityEvidence(
                                custodian.ApprovalId,
                                custodian.WitnessSequence,
                                custodian.WitnessEpoch,
                                custodian.WitnessEntryHash,
                                custodian.CandidateSha256,
                                CancellationToken.None
                            )

                        return Some(owner, custodian)
        }
