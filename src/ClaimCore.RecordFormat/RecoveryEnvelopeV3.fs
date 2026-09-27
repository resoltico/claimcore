namespace ClaimCore.RecordFormat

open System
open System.Security.Cryptography
open RecoveryArtifactEncoding

module RecoveryEnvelopeV3 =
    let encode maximumBytes encryptionKey macKey (artifact: RecoveryArtifactV3) =
        if maximumBytes < 1 || not (validKeys encryptionKey macKey) then
            invalidArg (nameof encryptionKey) "The artifact key policy is invalid."

        if not (validShape maximumBytes artifact) then
            invalidArg (nameof artifact) "The recovery artifact identity is invalid."

        let payload = plain artifact
        let ciphertext = Array.zeroCreate<byte> payload.Length
        let tag = Array.zeroCreate<byte> tagLength

        try
            use cipher = new AesGcm(encryptionKey, tagLength)
            cipher.Encrypt(artifact.Nonce, payload, ciphertext, tag, metadata artifact)
            let unsigned = outer artifact ciphertext tag None
            let mac = HMACSHA256.HashData(macKey, unsigned) |> Convert.ToHexStringLower
            outer artifact ciphertext tag (Some mac)
        finally
            CryptographicOperations.ZeroMemory(payload)

    let private outerShape root =
        Json.properties
            "$"
            [
                "format"
                "formatVersion"
                "keyId"
                "exportId"
                "installationId"
                "epoch"
                "caseId"
                "preparerActorId"
                "preparerGrantRevision"
                "importerActorId"
                "exporterActorId"
                "exporterGrantRevision"
                "operationId"
                "issuedAt"
                "expiresAt"
                "canonicalCommandFormat"
                "requestFingerprintVersion"
                "nonceBase64"
                "ciphertextBase64"
                "tagBase64"
                "macSha256"
            ]
            root

        if
            Json.text "$" "format" root <> format
            || Json.integer "$" "formatVersion" root <> int64 version
        then
            Json.reject "$" "The recovery artifact format is unsupported."

    let private read maximumBytes root =
        outerShape root
        let ciphertextText = Json.text "$" "ciphertextBase64" root

        if int64 ciphertextText.Length > int64 maximumBytes * 3L then
            Json.reject "$" "The encrypted request is too large."

        let ciphertext =
            try
                Convert.FromBase64String(ciphertextText)
            with :? FormatException ->
                Json.reject "$" "The encrypted request is invalid."

        if Convert.ToBase64String(ciphertext) <> ciphertextText then
            Json.reject "$" "The encrypted request is not canonical."

        let importer =
            Json.optionalString "$" "importerActorId" root
            |> Option.map (id "$.importerActorId")

        let commandFormat = Json.integer "$" "canonicalCommandFormat" root
        let fingerprintVersion = Json.integer "$" "requestFingerprintVersion" root

        if
            commandFormat <> int64 RecordVersions.CanonicalCommandFormat
            || fingerprintVersion <> int64 RecordVersions.RequestFingerprint
        then
            Json.reject "$" "The request format is unsupported."

        let artifact =
            {
                KeyId = Json.text "$" "keyId" root |> id "$.keyId"
                ExportId = Json.text "$" "exportId" root |> id "$.exportId"
                InstallationId = Json.text "$" "installationId" root |> id "$.installationId"
                Epoch = Json.integer "$" "epoch" root
                CaseId = Json.text "$" "caseId" root |> id "$.caseId"
                PreparerActorId = Json.text "$" "preparerActorId" root |> id "$.preparerActorId"
                PreparerGrantRevision = Json.integer "$" "preparerGrantRevision" root
                ImporterActorId = importer
                ExporterActorId = Json.text "$" "exporterActorId" root |> id "$.exporterActorId"
                ExporterGrantRevision = Json.integer "$" "exporterGrantRevision" root
                OperationId = Json.text "$" "operationId" root |> id "$.operationId"
                IssuedAt = Json.text "$" "issuedAt" root |> moment "$.issuedAt"
                ExpiresAt = Json.text "$" "expiresAt" root |> moment "$.expiresAt"
                CanonicalCommandFormat = int commandFormat
                RequestFingerprintVersion = int fingerprintVersion
                Nonce = Json.text "$" "nonceBase64" root |> base64 "$.nonceBase64" 12
                CanonicalRequest = [||]
            }

        let tag = Json.text "$" "tagBase64" root |> base64 "$.tagBase64" tagLength
        let mac = Json.text "$" "macSha256" root
        artifact, ciphertext, tag, mac

    let private decrypted maximumBytes (artifact: RecoveryArtifactV3) (payload: byte array) =
        match
            Json.parse payload (fun root ->
                Json.properties "$" [ "requestSha256"; "canonicalRequestBase64" ] root
                let digest = Json.text "$" "requestSha256" root
                let encoded = Json.text "$" "canonicalRequestBase64" root

                let canonical =
                    try
                        Convert.FromBase64String(encoded)
                    with :? FormatException ->
                        Json.reject "$" "The canonical request is invalid."

                if
                    canonical.Length = 0
                    || canonical.Length > maximumBytes
                    || Convert.ToBase64String(canonical) <> encoded
                    || (canonical |> SHA256.HashData |> Convert.ToHexStringLower) <> digest
                then
                    CryptographicOperations.ZeroMemory(canonical)
                    Json.reject "$" "The canonical request failed integrity checks."

                canonical)
        with
        | Error _ -> Error "RECOVERY_ARTIFACT_INVALID"
        | Ok canonical ->
            let recovered =
                { artifact with
                    CanonicalRequest = canonical
                }

            if validShape maximumBytes recovered then
                Ok recovered
            else
                CryptographicOperations.ZeroMemory(canonical)
                Error "RECOVERY_ARTIFACT_INVALID"

    let private validMac
        (artifact: RecoveryArtifactV3)
        (ciphertext: byte array)
        (tag: byte array)
        (macKey: byte array)
        (mac: string)
        (source: byte array)
        =
        let unsigned = outer artifact ciphertext tag None
        let expected = HMACSHA256.HashData(macKey, unsigned)

        try
            let supplied =
                try
                    Convert.FromHexString(mac)
                with :? FormatException ->
                    [||]

            mac.Length = 64
            && (mac
                |> Seq.forall (fun character ->
                    ('0' <= character && character <= '9')
                    || ('a' <= character && character <= 'f')))
            && supplied.Length = expected.Length
            && CryptographicOperations.FixedTimeEquals(supplied, expected)
            && CryptographicOperations.FixedTimeEquals(
                outer artifact ciphertext tag (Some mac),
                source
            )
        finally
            CryptographicOperations.ZeroMemory(expected)

    let private decryptArtifact
        maximumBytes
        (artifact: RecoveryArtifactV3)
        (encryptionKey: byte array)
        (ciphertext: byte array)
        (tag: byte array)
        =
        let payload = Array.zeroCreate<byte> ciphertext.Length

        try
            try
                use cipher = new AesGcm(encryptionKey, tagLength)
                cipher.Decrypt(artifact.Nonce, ciphertext, tag, payload, metadata artifact)
                decrypted maximumBytes artifact payload
            with :? CryptographicException ->
                Error "RECOVERY_ARTIFACT_AUTHENTICATION_FAILED"
        finally
            CryptographicOperations.ZeroMemory(payload)

    let private verified
        maximumBytes
        (resolveKeys: Guid -> (byte array * byte array) option)
        expectedInstallation
        expectedEpoch
        now
        maximumLifetime
        (source: byte array)
        ((artifact, ciphertext, tag, mac): RecoveryArtifactV3 * byte array * byte array * string)
        =
        let resolved =
            try
                Ok(resolveKeys artifact.KeyId)
            with _ ->
                Error "RECOVERY_ARTIFACT_KEY_UNAVAILABLE"

        match resolved with
        | Error reason -> Error reason
        | Ok None -> Error "RECOVERY_ARTIFACT_KEY_UNKNOWN"
        | Ok(Some(encryptionKey, macKey)) when validKeys encryptionKey macKey ->
            if not (validMac artifact ciphertext tag macKey mac source) then
                Error "RECOVERY_ARTIFACT_AUTHENTICATION_FAILED"
            elif
                artifact.InstallationId <> expectedInstallation
                || artifact.Epoch <> expectedEpoch
                || artifact.IssuedAt > now
                || artifact.ExpiresAt <= now
                || artifact.ExpiresAt - artifact.IssuedAt > maximumLifetime
            then
                Error "RECOVERY_ARTIFACT_EXPIRED_OR_WRONG_EPOCH"
            else
                decryptArtifact maximumBytes artifact encryptionKey ciphertext tag
        | _ -> Error "RECOVERY_ARTIFACT_KEY_INVALID"

    let decode
        maximumBytes
        resolveKeys
        expectedInstallation
        expectedEpoch
        now
        maximumLifetime
        (source: byte array)
        =
        if maximumBytes < 1 || maximumLifetime <= TimeSpan.Zero then
            invalidArg (nameof maximumBytes) "Artifact bounds must be positive."

        if source.Length = 0 || int64 source.Length > int64 maximumBytes * 3L + 4096L then
            Error "RECOVERY_ARTIFACT_INVALID"
        else
            match Json.parse source (read maximumBytes) with
            | Error _ -> Error "RECOVERY_ARTIFACT_INVALID"
            | Ok parsed ->
                verified
                    maximumBytes
                    resolveKeys
                    expectedInstallation
                    expectedEpoch
                    now
                    maximumLifetime
                    source
                    parsed

    /// Only a bounded routing hint. Callers must authenticate the same bytes before using either
    /// identity for authorization, disclosure, or a mutation.
    let tryPeekIdentity maximumBytes (source: byte array) =
        if
            maximumBytes < 1
            || source.Length = 0
            || int64 source.Length > int64 maximumBytes * 3L + 4096L
        then
            None
        else
            match Json.parse source (read maximumBytes) with
            | Error _ -> None
            | Ok(artifact, _, _, _) -> Some(artifact.OperationId, artifact.CaseId)
