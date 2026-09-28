namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Text.Json
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.HostSecurity
open ClaimCore.Witness

module internal DatabaseRestoreSignedEvidence =
    let private read maximum path =
        match PrivateFileService.readBinary maximum path with
        | Ok bytes when bytes.Length > 0 -> bytes
        | _ -> invalidOp "Private restored-pair evidence is unavailable."

    let private signature path =
        let bytes = read 64 path

        if bytes.Length <> 64 then
            CryptographicOperations.ZeroMemory(bytes)
            invalidOp "Restored-pair signature length is invalid."

        bytes

    /// Caller holds authority_tip while checking the current human holder and grant.
    let signer (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) id purpose =
        use command =
            new NpgsqlCommand(
                "SELECT s.ed25519_public_key,s.active,s.signer_purpose,s.holder_actor_id "
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

        command.Parameters.AddWithValue("key", NpgsqlDbType.Uuid, id) |> ignore
        use reader = command.ExecuteReader()

        if
            not (reader.Read())
            || not (reader.GetBoolean(1))
            || reader.GetString(2) <> ManagedCopySignerCandidate.purposeName purpose
        then
            invalidOp "Restored-pair signer is not currently witnessed and active."

        let key = reader.GetFieldValue<byte array>(0)
        let holder = reader.GetGuid(3)

        if reader.Read() || key.Length <> 32 then
            CryptographicOperations.ZeroMemory(key)
            invalidOp "Restored-pair signer is ambiguous."

        key, holder

    /// A completed W2 may be retried after key retirement. The immutable REGISTER
    /// event and its exact witness entry, not current active/grant state, authenticate
    /// the old signed bytes. This cannot qualify a new activation.
    let historicalSigner
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        id
        purpose
        epoch
        cutoff
        =
        use command =
            new NpgsqlCommand(
                "SELECT s.ed25519_public_key,s.holder_actor_id,s.signer_purpose,"
                + "e.witness_sequence,e.witness_entry_hash "
                + "FROM claimcore.managed_copy_signers s "
                + "JOIN claimcore.managed_copy_signer_events e "
                + "ON e.signing_key_id=s.signing_key_id AND e.action_name='REGISTER' "
                + "WHERE s.signing_key_id=@key AND e.witness_epoch=@epoch "
                + "AND e.witness_sequence<=@cutoff",
                connection,
                transaction
            )

        command.Parameters.AddWithValue("key", NpgsqlDbType.Uuid, id) |> ignore
        command.Parameters.AddWithValue("epoch", NpgsqlDbType.Bigint, epoch) |> ignore
        command.Parameters.AddWithValue("cutoff", NpgsqlDbType.Bigint, cutoff) |> ignore
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Historical signer registration is absent."

        let key = reader.GetFieldValue<byte array>(0)
        let holder = reader.GetGuid(1)
        let registeredPurpose = reader.GetString(2)
        let sequence = reader.GetInt64(3)
        let entryHash = reader.GetFieldValue<byte array>(4)

        if
            reader.Read()
            || key.Length <> 32
            || registeredPurpose <> ManagedCopySignerCandidate.purposeName purpose
        then
            CryptographicOperations.ZeroMemory(key)
            invalidOp "Historical signer registration is ambiguous."

        reader.Close()

        match witness.TryReadHashAtSequence(sequence) with
        | Some hash when CryptographicOperations.FixedTimeEquals(hash, entryHash) -> key, holder
        | _ ->
            CryptographicOperations.ZeroMemory(key)
            invalidOp "Historical signer registration lacks a witness entry."

    let verifyBytes key (bytes: byte array) (signatureBytes: byte array) =
        if not (ManagedCopySignature.verify key bytes signatureBytes) then
            invalidOp "Restored-pair signature failed independent verification."

    let checkpointMatches
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        (publicKey: byte array)
        =
        if report.CustodyKeyId <> index.CheckpointSignerKeyId || publicKey.Length <> 32 then
            false
        else
            let digest = SHA256.HashData(publicKey)

            try
                try
                    let claimed = Convert.FromHexString(report.CustodyPublicKeySha256)

                    claimed.Length = 32
                    && CryptographicOperations.FixedTimeEquals(digest.AsSpan(), claimed.AsSpan())
                with _ ->
                    false
            finally
                CryptographicOperations.ZeroMemory(digest)

    let verifyFileWith maximum expectedSha key contentPath signaturePath =
        let bytes = read maximum contentPath

        try
            let actual = bytes |> SHA256.HashData |> Convert.ToHexStringLower

            match expectedSha with
            | Some expected when actual <> expected ->
                invalidOp "Restored-pair evidence digest changed."
            | _ -> ()

            let signed = signature signaturePath

            try
                verifyBytes key bytes signed
            finally
                CryptographicOperations.ZeroMemory(signed)

            match DatabaseRestoreCanonical.parse (Array.copy bytes) with
            | None -> invalidOp "Restored-pair evidence is not canonical."
            | Some document -> document
        finally
            CryptographicOperations.ZeroMemory(bytes)

    let verifyFile maximum expectedSha key contentPath signaturePath =
        verifyFileWith maximum (Some expectedSha) key contentPath signaturePath

    let verifyReport key (files: RestoreReportFiles) =
        verifyBytes key files.Report files.Signature

    let sha (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexStringLower

    let exactText name (root: JsonElement) expected =
        if DatabaseRestoreCanonical.text name root <> expected then
            invalidOp "Restored-pair evidence identity differs."

    let exactNumber name (root: JsonElement) expected =
        if DatabaseRestoreCanonical.number name root <> expected then
            invalidOp "Restored-pair evidence cutoff differs."
