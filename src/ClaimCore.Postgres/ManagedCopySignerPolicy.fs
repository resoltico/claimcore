namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application

module internal ManagedCopySignerPolicy =
    let databaseNow connection transaction =
        task {
            use command = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction)
            let! value = command.ExecuteScalarAsync()

            return
                match value with
                | :? DateTimeOffset as instant -> instant
                | :? DateTime as instant when instant.Kind = DateTimeKind.Utc ->
                    DateTimeOffset instant
                | _ -> invalidOp "Database approval clock is unavailable."
        }

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
        if
            first.ApprovalId = second.ApprovalId
            || first.ActorId = second.ActorId
            || not (
                exactApproval now keyId action purpose publicHash first
                && exactApproval now keyId action purpose publicHash second
            )
        then
            None
        else
            match ordered first second with
            | None -> None
            | Some(owner, custodian) ->
                if
                    owner.HolderApprovalId <> Some custodian.ApprovalId
                    || custodian.HolderApprovalId.IsSome
                then
                    None
                else
                    witness.VerifyAuthorityEvidence(
                        owner.ApprovalId,
                        owner.WitnessSequence,
                        owner.WitnessEpoch,
                        owner.WitnessEntryHash,
                        owner.CandidateSha256
                    )

                    witness.VerifyAuthorityEvidence(
                        custodian.ApprovalId,
                        custodian.WitnessSequence,
                        custodian.WitnessEpoch,
                        custodian.WitnessEntryHash,
                        custodian.CandidateSha256
                    )

                    Some(owner, custodian)
