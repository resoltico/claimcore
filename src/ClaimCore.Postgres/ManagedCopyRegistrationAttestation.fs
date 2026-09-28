namespace ClaimCore.Postgres

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json

/// The signed input is byte-exact Python/JSON canonical form; semantic fields are closed.
module internal ManagedCopyRegistrationAttestation =
    let fields =
        set
            [
                "backupManifestSha256"
                "capturedAt"
                "ciphertextBytes"
                "ciphertextSha256"
                "cluster"
                "copyId"
                "copyRevision"
                "custodianCommitment"
                "cycleId"
                "deletionProofSha256"
                "encryptionKeyId"
                "epoch"
                "eventId"
                "eventKind"
                "format"
                "installationId"
                "kind"
                "lastVerifiedAt"
                "lineageId"
                "locationCommitment"
                "postgresSystemId"
                "previousEventHash"
                "primaryRegistration"
                "retainUntil"
                "signingKeyId"
                "sourceCaseId"
                "state"
                "timeline"
                "verificationProofSha256"
                "walEndLsn"
                "walSegment"
                "walSegmentBytes"
                "walStartLsn"
                "witnessCutoffHash"
                "witnessCutoffSequence"
            ]

    let canonicalBytes (root: JsonElement) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()

        root.EnumerateObject()
        |> Seq.sortBy _.Name
        |> Seq.iter (fun property ->
            writer.WritePropertyName(property.Name)
            property.Value.WriteTo(writer))

        writer.WriteEndObject()
        writer.Flush()
        Array.append (stream.ToArray()) [| byte '\n' |]

    let requiredText (root: JsonElement) (name: string) =
        let value = root.GetProperty(name)

        if value.ValueKind <> JsonValueKind.String then
            invalidOp "Managed-copy attestation field type is invalid."

        value.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Null text.")

    let optionalText (root: JsonElement) (name: string) =
        let value = root.GetProperty(name)

        match value.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String -> Some(requiredText root name)
        | _ -> invalidOp "Managed-copy attestation field type is invalid."

    let private uuid (root: JsonElement) (name: string) =
        let text = requiredText root name
        let value = Guid.ParseExact(text, "D")

        if value = Guid.Empty || value.ToString("D") <> text then
            invalidOp "Managed-copy UUID is noncanonical."

        value

    let optionalUuid (root: JsonElement) (name: string) =
        match optionalText root name with
        | None -> None
        | Some text ->
            let value = Guid.ParseExact(text, "D")

            if value = Guid.Empty || value.ToString("D") <> text then
                invalidOp "Managed-copy UUID is noncanonical."

            Some value

    let number (root: JsonElement) (name: string) =
        let value = root.GetProperty(name)

        if value.ValueKind <> JsonValueKind.Number then
            invalidOp "Managed-copy number type is invalid."

        value.GetInt64()

    let private optionalInteger (root: JsonElement) (name: string) =
        let value = root.GetProperty(name)

        match value.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.Number -> Some(value.GetInt32())
        | _ -> invalidOp "Managed-copy integer type is invalid."

    let hex (text: string) =
        if text.Length <> 64 || text <> text.ToLowerInvariant() then
            invalidOp "Managed-copy digest is noncanonical."

        let decoded: byte array = Convert.FromHexString(text)

        if Convert.ToHexStringLower(decoded) <> text then
            invalidOp "Managed-copy digest is invalid."

        decoded

    let optionalHex (root: JsonElement) (name: string) =
        optionalText root name |> Option.map hex

    let time (root: JsonElement) (name: string) =
        let text = requiredText root name

        DateTimeOffset.ParseExact(
            text,
            "yyyy-MM-ddTHH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
        )

    let optionalTime (root: JsonElement) (name: string) =
        match optionalText root name with
        | None -> None
        | Some _ -> Some(time root name)

    let private shape (root: JsonElement) =
        let properties = root.EnumerateObject() |> Seq.toArray

        root.ValueKind = JsonValueKind.Object
        && properties.Length = fields.Count
        && (properties |> Seq.map _.Name |> Set.ofSeq) = fields
        && requiredText root "format" = "claimcore-managed-copy-attestation-1"
        && requiredText root "eventKind" = "REGISTER"
        && requiredText root "state" = "UNVERIFIED"
        && requiredText root "primaryRegistration" = "NOT_REGISTERED"
        && number root "copyRevision" = 1L
        && root.GetProperty("previousEventHash").ValueKind = JsonValueKind.Null
        && root.GetProperty("lastVerifiedAt").ValueKind = JsonValueKind.Null
        && root.GetProperty("verificationProofSha256").ValueKind = JsonValueKind.Null
        && root.GetProperty("deletionProofSha256").ValueKind = JsonValueKind.Null

    let expectedKind (value: ManagedCopyAttestation) =
        let pgFields =
            value.PostgresSystemId.IsSome
            && value.Timeline.IsSome
            && value.WalSegmentBytes.IsSome
            && (value.Cluster = "PRIMARY" || value.Cluster = "WITNESS")

        let noPgFields =
            value.PostgresSystemId.IsNone
            && value.Timeline.IsNone
            && value.WalSegmentBytes.IsNone

        let noArchiveFields =
            value.BackupManifestSha256.IsNone
            && value.WalStartLsn.IsNone
            && value.WalEndLsn.IsNone
            && value.WalSegment.IsNone
            && value.CycleId.IsNone

        let baseFields =
            value.BackupManifestSha256.IsSome
            && value.WalStartLsn.IsSome
            && value.WalEndLsn.IsSome
            && value.WalSegment.IsNone
            && value.CycleId.IsSome

        let walFields =
            value.BackupManifestSha256.IsNone
            && value.WalStartLsn.IsNone
            && value.WalEndLsn.IsNone
            && value.WalSegment.IsSome
            && value.CycleId.IsNone

        match value.Kind with
        | "BASE" -> pgFields && value.SourceCaseId.IsNone && baseFields
        | "WAL" -> pgFields && value.SourceCaseId.IsNone && walFields
        | "SNAPSHOT"
        | "REPLICA" -> pgFields && value.SourceCaseId.IsNone && noArchiveFields
        | "WITNESS_PAYLOAD" ->
            value.Cluster = "WITNESS"
            && value.SourceCaseId.IsSome
            && noPgFields
            && noArchiveFields
        | "EXPORT" ->
            value.Cluster = "NONE"
            && value.SourceCaseId.IsSome
            && noPgFields
            && noArchiveFields
        | "ENCRYPTION_KEY_COPY" ->
            value.Cluster = "NONE"
            && value.SourceCaseId.IsNone
            && noPgFields
            && noArchiveFields
        | _ -> false

    let decode (root: JsonElement) =
        {
            EventId = uuid root "eventId"
            CopyId = uuid root "copyId"
            InstallationId = uuid root "installationId"
            LineageId = uuid root "lineageId"
            Epoch = number root "epoch"
            Cluster = requiredText root "cluster" |> _.ToUpperInvariant()
            Kind = requiredText root "kind"
            SourceCaseId = optionalUuid root "sourceCaseId"
            PostgresSystemId = optionalText root "postgresSystemId"
            Timeline = optionalInteger root "timeline"
            WalSegmentBytes = optionalInteger root "walSegmentBytes"
            BackupManifestSha256 = optionalHex root "backupManifestSha256"
            WalStartLsn = optionalText root "walStartLsn"
            WalEndLsn = optionalText root "walEndLsn"
            WalSegment = optionalText root "walSegment"
            WitnessCutoffSequence = number root "witnessCutoffSequence"
            WitnessCutoffHash = requiredText root "witnessCutoffHash" |> hex
            CiphertextSha256 = requiredText root "ciphertextSha256" |> hex
            CiphertextBytes = number root "ciphertextBytes"
            EncryptionKeyId = uuid root "encryptionKeyId"
            SigningKeyId = uuid root "signingKeyId"
            CustodianCommitment = requiredText root "custodianCommitment" |> hex
            LocationCommitment = requiredText root "locationCommitment" |> hex
            CapturedAt = time root "capturedAt"
            RetainUntil = time root "retainUntil"
            VerificationProofSha256 = optionalHex root "verificationProofSha256"
            CycleId = optionalUuid root "cycleId"
        }

    let private valid (value: ManagedCopyAttestation) =
        value.Epoch > 0L
        && value.WitnessCutoffSequence >= 0L
        && value.CiphertextBytes > 0L
        && value.RetainUntil > value.CapturedAt
        && value.RetainUntil <= value.CapturedAt.AddYears(10)
        && value.VerificationProofSha256.IsNone
        && expectedKind value

    let parse (bytes: byte array) =
        if isNull (box bytes) || bytes.Length = 0 || bytes.Length > 300000 then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
                let root = document.RootElement

                if not (shape root) || canonicalBytes root <> bytes then
                    None
                else
                    let value = decode root
                    if valid value then Some value else None
            with _ ->
                None
