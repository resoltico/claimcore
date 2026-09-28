namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application

type internal SignerApprovalEvidence =
    {
        ApprovalId: Guid
        SigningKeyId: Guid
        Action: CopySignerAction
        Purpose: CopySignerPurpose
        HolderApprovalId: Guid option
        PublicKeySha256: byte array
        ActorId: Guid
        ActorRole: string
        GrantRevision: int64
        ExpiresAt: DateTimeOffset
        Canonical: byte array
        CandidateSha256: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessEntryHash: byte array
        CurrentActorAndGrant: bool
        Used: bool
    }

/// Reads only retained, witnessed approvals. Actor and grant state is checked under authority_tip.
module internal ManagedCopySignerApprovalRead =
    let private evidence approvalId (reader: DbDataReader) =
        let action =
            match reader.GetString(1) with
            | "REGISTER" -> CopySignerAction.Register
            | "RETIRE" -> CopySignerAction.Retire
            | _ -> invalidOp "Signer approval action is invalid."

        {
            ApprovalId = approvalId
            SigningKeyId = reader.GetGuid(0)
            Action = action
            Purpose = ManagedCopySignerCandidate.purposeOfName (reader.GetString(14))
            HolderApprovalId =
                if reader.IsDBNull(15) then
                    None
                else
                    Some(reader.GetGuid(15))
            PublicKeySha256 = reader.GetFieldValue<byte array>(2)
            ActorId = reader.GetGuid(3)
            ActorRole = reader.GetString(4)
            GrantRevision = reader.GetInt64(5)
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(6)
            Canonical = reader.GetFieldValue<byte array>(7)
            CandidateSha256 = reader.GetFieldValue<byte array>(8)
            WitnessSequence = reader.GetInt64(9)
            WitnessEpoch = reader.GetInt64(10)
            WitnessEntryHash = reader.GetFieldValue<byte array>(11)
            CurrentActorAndGrant = reader.GetBoolean(12)
            Used = reader.GetBoolean(13)
        }

    let load (connection: NpgsqlConnection) transaction approvalId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT a.signing_key_id,a.action_name,a.public_key_sha256,a.actor_id,"
                    + "a.actor_role,a.grant_revision,a.expires_at,a.canonical_action,"
                    + "a.candidate_sha256,a.witness_sequence,a.witness_epoch,"
                    + "a.witness_entry_hash,"
                    + "(actor.enabled AND actor.principal_kind='HUMAN' "
                    + "AND actor.changed_revision<=a.grant_revision AND EXISTS "
                    + "(SELECT 1 FROM claimcore.actor_grants g WHERE g.actor_id=a.actor_id "
                    + "AND g.scope_kind='INSTALLATION' AND g.active "
                    + "AND g.role_name=a.actor_role AND g.changed_revision<=a.grant_revision)),"
                    + "EXISTS(SELECT 1 FROM claimcore.managed_copy_signer_approval_uses u "
                    + "WHERE u.approval_id=a.approval_id),a.signer_purpose,a.holder_approval_id "
                    + "FROM claimcore.managed_copy_signer_approvals a "
                    + "JOIN claimcore.actors actor ON actor.actor_id=a.actor_id "
                    + "WHERE a.approval_id=@approval",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! found = reader.ReadAsync()

            return if not found then None else Some(evidence approvalId reader)
        }

    let canonicalMatches (value: SignerApprovalEvidence) =
        let role =
            match value.ActorRole, value.HolderApprovalId with
            | "OWNER", Some holder -> CopySignerApprovalRole.Owner holder
            | ("AUDITOR_CUSTODIAN" | "DATA_STEWARD"), None -> CopySignerApprovalRole.Custodian
            | _ -> invalidOp "Signer approval holder shape is invalid."

        let request: CopySignerApprovalRequest =
            {
                ApprovalId = value.ApprovalId
                SigningKeyId = value.SigningKeyId
                Action = value.Action
                Purpose = value.Purpose
                PublicKeySha256 = value.PublicKeySha256
                Role = role
                ExpiresAt = value.ExpiresAt
            }

        let expected =
            ManagedCopySignerCandidate.approval
                request
                value.ActorId
                value.ActorRole
                value.GrantRevision

        try
            value.Canonical = expected
            && value.CandidateSha256.Length = 32
            && CryptographicOperations.FixedTimeEquals(
                ReadOnlySpan<byte>(SHA256.HashData(value.Canonical)),
                ReadOnlySpan<byte>(value.CandidateSha256)
            )
        finally
            CryptographicOperations.ZeroMemory(expected)
