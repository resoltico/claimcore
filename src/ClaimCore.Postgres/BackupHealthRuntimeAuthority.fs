namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes

module internal BackupHealthRuntimeAuthority =
    let private signer
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (claim: BackupHealthClaims)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT s.ed25519_public_key,s.holder_actor_id,s.active,s.signer_purpose "
                    + "FROM claimcore.managed_copy_signers s "
                    + "JOIN claimcore.actors a ON a.actor_id=s.holder_actor_id "
                    + "WHERE s.signing_key_id=@key AND a.enabled AND a.principal_kind='HUMAN' "
                    + "AND EXISTS (SELECT 1 FROM claimcore.actor_grants g "
                    + "WHERE g.actor_id=a.actor_id AND g.active "
                    + "AND g.scope_kind='INSTALLATION' "
                    + "AND g.scope_case_id='00000000-0000-0000-0000-000000000000' "
                    + "AND g.role_name IN ('AUDITOR_CUSTODIAN','DATA_STEWARD'))",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("key", NpgsqlDbType.Uuid, claim.SignerKeyId)
            |> ignore

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                invalidOp "Backup health CHECKPOINT signer is unavailable."

            let key = reader.GetFieldValue<byte array>(0)
            let holder = reader.GetGuid(1)
            let active = reader.GetBoolean(2)
            let purpose = reader.GetString(3)

            let! duplicated = reader.ReadAsync(ct)

            if
                duplicated
                || key.Length <> 32
                || not active
                || purpose <> "CHECKPOINT"
                || holder <> claim.SignerHolderActorId
            then
                CryptographicOperations.ZeroMemory(key.AsSpan())
                invalidOp "Backup health CHECKPOINT signer is not current."

            return key
        }

    let verifySigner
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (claim: BackupHealthClaims)
        (canonical: byte array)
        (signature: byte array)
        (ct: CancellationToken)
        =
        task {
            let! key = signer connection transaction claim ct

            try
                if not (ManagedCopySignature.verify key canonical signature) then
                    invalidOp "Backup health detached signature is invalid."
            finally
                CryptographicOperations.ZeroMemory(key.AsSpan())
        }

    let verifyLineage
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (claim: BackupHealthClaims)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT installation_id,lineage_id,witness_epoch,writer_generation "
                    + "FROM claimcore.installation_lineage WHERE singleton",
                    connection,
                    transaction
                )

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                invalidOp "Backup health installation is unavailable."

            let matching =
                reader.GetGuid(0) = claim.InstallationId
                && reader.GetGuid(1) = claim.LineageId
                && reader.GetInt64(2) = claim.Epoch
                && reader.GetInt64(3) = claim.WriterGeneration

            let! duplicated = reader.ReadAsync(ct)

            if duplicated || not matching then
                invalidOp "Backup health installation or writer generation diverges."
        }

    let verifyRevision
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (claim: BackupHealthClaims)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT revision FROM claimcore.authority_tip WHERE singleton",
                    connection,
                    transaction
                )

            let! result = command.ExecuteScalarAsync(ct)

            match result with
            | :? int64 as revision when revision >= claim.AuthorityRevision -> return ()
            | _ -> return invalidOp "Backup health authority revision rolled back."
        }
