namespace ClaimCore.Postgres

open System
open System.Text.Json
open System.Text.RegularExpressions

/// Strict canonical proof parser. Execution independently checks its signature and object bytes.
module internal ManagedCopyPhysicalProofCodec =
    let private identityFields =
        [
            "format"
            "source"
            "purpose"
            "verificationEventId"
            "nonce"
            "copyId"
            "copyEventId"
            "copyRevision"
            "archiveObjectId"
            "installationId"
            "lineageId"
            "witnessEpoch"
            "witnessCutoffSequence"
            "witnessCutoffHash"
            "cluster"
            "kind"
            "postgresSystemId"
            "timeline"
            "walSegmentBytes"
            "backupManifestSha256"
            "walStartLsn"
            "walEndLsn"
            "walSegment"
            "locationCommitment"
            "ciphertextSha256"
            "ciphertextBytes"
            "decryptedSha256"
            "decryptedBytes"
        ]

    let private inspectionFields =
        [
            "pgVerifyBackupManifestSha256"
            "recoveredPostgresSystemId"
            "recoveredTimeline"
            "recoveredInstallationId"
            "recoveredLineageId"
            "recoveredEpoch"
            "recoveredRowCount"
            "walFirstRecordParsed"
            "walTimelineMatched"
            "pgVerifyBackup"
            "isolatedBoot"
            "recoveredIdentityChecked"
            "ciphertextRehashed"
            "decrypted"
            "verifierSigningKeyId"
            "verifierHolderActorId"
            "checkedAt"
            "validUntil"
            "fullPairReady"
            "realDataReady"
        ]

    let private fields = Set.ofList (identityFields @ inspectionFields)

    let private text root name =
        ManagedCopyRegistrationAttestation.requiredText root name

    let private optionalText root name =
        ManagedCopyRegistrationAttestation.optionalText root name

    let private id root name =
        let raw = text root name
        let value = Guid.ParseExact(raw, "D")

        if value = Guid.Empty || value.ToString("D") <> raw then
            invalidOp "Physical copy identity is noncanonical."

        value

    let private optionalId root name =
        optionalText root name
        |> Option.map (fun raw ->
            let value = Guid.ParseExact(raw, "D")

            if value = Guid.Empty || value.ToString("D") <> raw then
                invalidOp "Physical recovered identity is noncanonical."

            value)

    let private digest root name =
        text root name |> ManagedCopyRegistrationAttestation.hex

    let private optionalDigest root name =
        ManagedCopyRegistrationAttestation.optionalHex root name

    let private number root name =
        ManagedCopyRegistrationAttestation.number root name

    let private optionalNumber (root: JsonElement) (name: string) =
        let item = root.GetProperty(name)

        match item.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.Number -> Some(item.GetInt64())
        | _ -> invalidOp "Physical copy number is invalid."

    let private flag (root: JsonElement) (name: string) =
        let item = root.GetProperty(name)

        if item.ValueKind <> JsonValueKind.True && item.ValueKind <> JsonValueKind.False then
            invalidOp "Physical copy flag is invalid."

        item.GetBoolean()

    let private decode (root: JsonElement) =
        {
            VerificationEventId = id root "verificationEventId"
            Nonce = digest root "nonce"
            CopyId = id root "copyId"
            CopyEventId = id root "copyEventId"
            CopyRevision = number root "copyRevision"
            ArchiveObjectId = id root "archiveObjectId"
            InstallationId = id root "installationId"
            LineageId = id root "lineageId"
            Epoch = number root "witnessEpoch"
            WitnessCutoffSequence = number root "witnessCutoffSequence"
            WitnessCutoffHash = digest root "witnessCutoffHash"
            Cluster = text root "cluster"
            Kind = text root "kind"
            PostgresSystemId = text root "postgresSystemId"
            Timeline = int (number root "timeline")
            WalSegmentBytes = int (number root "walSegmentBytes")
            BackupManifestSha256 = optionalDigest root "backupManifestSha256"
            WalStartLsn = optionalText root "walStartLsn"
            WalEndLsn = optionalText root "walEndLsn"
            WalSegment = optionalText root "walSegment"
            LocationCommitment = digest root "locationCommitment"
            CiphertextSha256 = digest root "ciphertextSha256"
            CiphertextBytes = number root "ciphertextBytes"
            DecryptedSha256 = digest root "decryptedSha256"
            DecryptedBytes = number root "decryptedBytes"
            PgVerifyBackupManifestSha256 = optionalDigest root "pgVerifyBackupManifestSha256"
            RecoveredPostgresSystemId = optionalText root "recoveredPostgresSystemId"
            RecoveredTimeline = optionalNumber root "recoveredTimeline" |> Option.map int
            RecoveredInstallationId = optionalId root "recoveredInstallationId"
            RecoveredLineageId = optionalId root "recoveredLineageId"
            RecoveredEpoch = optionalNumber root "recoveredEpoch"
            RecoveredRowCount = optionalNumber root "recoveredRowCount"
            WalFirstRecordParsed = flag root "walFirstRecordParsed"
            WalTimelineMatched = flag root "walTimelineMatched"
            PgVerifyBackup = flag root "pgVerifyBackup"
            IsolatedBoot = flag root "isolatedBoot"
            RecoveredIdentityChecked = flag root "recoveredIdentityChecked"
            CiphertextRehashed = flag root "ciphertextRehashed"
            Decrypted = flag root "decrypted"
            VerifierSigningKeyId = id root "verifierSigningKeyId"
            VerifierHolderActorId = id root "verifierHolderActorId"
            CheckedAt = ManagedCopyRegistrationAttestation.time root "checkedAt"
            ValidUntil = ManagedCopyRegistrationAttestation.time root "validUntil"
        }

    let private lsn (value: string option) =
        value
        |> Option.exists (fun name -> Regex.IsMatch(name, "^[0-9A-F]{1,8}/[0-9A-F]{1,8}$"))

    let private baseShape (value: ManagedCopyPhysicalProof) =
        value.BackupManifestSha256.IsSome
        && value.PgVerifyBackupManifestSha256 = value.BackupManifestSha256
        && lsn value.WalStartLsn
        && lsn value.WalEndLsn
        && value.WalSegment.IsNone
        && value.RecoveredRowCount.IsSome
        && value.RecoveredRowCount.Value >= 0L
        && value.RecoveredPostgresSystemId = Some value.PostgresSystemId
        && value.RecoveredTimeline = Some value.Timeline
        && value.RecoveredInstallationId = Some value.InstallationId
        && value.RecoveredLineageId = Some value.LineageId
        && value.RecoveredEpoch = Some value.Epoch
        && value.PgVerifyBackup
        && value.IsolatedBoot
        && value.RecoveredIdentityChecked
        && not value.WalFirstRecordParsed
        && not value.WalTimelineMatched

    let private walShape (value: ManagedCopyPhysicalProof) =
        let walName =
            value.WalSegment
            |> Option.exists (fun name -> Regex.IsMatch(name, "^[0-9A-F]{24}$"))

        value.BackupManifestSha256.IsNone
        && value.PgVerifyBackupManifestSha256.IsNone
        && value.WalStartLsn.IsNone
        && value.WalEndLsn.IsNone
        && walName
        && value.RecoveredRowCount.IsNone
        && value.RecoveredPostgresSystemId.IsNone
        && value.RecoveredTimeline.IsNone
        && value.RecoveredInstallationId.IsNone
        && value.RecoveredLineageId.IsNone
        && value.RecoveredEpoch.IsNone
        && value.WalFirstRecordParsed
        && value.WalTimelineMatched
        && not value.PgVerifyBackup
        && not value.IsolatedBoot
        && not value.RecoveredIdentityChecked

    let private valid (value: ManagedCopyPhysicalProof) =
        value.CopyRevision >= 2L
        && value.Epoch > 0L
        && value.WitnessCutoffSequence >= 0L
        && (value.Cluster = "PRIMARY" || value.Cluster = "WITNESS")
        && Regex.IsMatch(value.PostgresSystemId, "^[0-9]{1,20}$")
        && value.Timeline > 0
        && value.WalSegmentBytes >= 1048576
        && value.WalSegmentBytes <= 1073741824
        && value.WalSegmentBytes &&& (value.WalSegmentBytes - 1) = 0
        && value.CiphertextBytes > 0L
        && value.DecryptedBytes > 0L
        && value.CheckedAt.Offset = TimeSpan.Zero
        && value.ValidUntil > value.CheckedAt
        && value.ValidUntil <= value.CheckedAt.AddMinutes(5.)
        && value.CiphertextRehashed
        && value.Decrypted
        && (if value.Kind = "BASE" then baseShape value
            elif value.Kind = "WAL" then walShape value
            else false)

    let parse (bytes: byte array) =
        if isNull (box bytes) || bytes.Length < 2 || bytes.Length > 16384 then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
                let root = document.RootElement
                let names = root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

                if
                    root.ValueKind <> JsonValueKind.Object
                    || names <> fields
                    || root.EnumerateObject() |> Seq.length <> fields.Count
                    || ManagedCopyRegistrationAttestation.canonicalBytes root <> bytes
                    || text root "format" <> "claimcore-managed-copy-physical-verification-1"
                    || text root "source" <> "ClaimCore.ManagedCopyVerifier"
                    || text root "purpose" <> "RESTORE_COPY_VERIFIER"
                    || flag root "fullPairReady"
                    || flag root "realDataReady"
                then
                    None
                else
                    let value = decode root
                    if valid value then Some value else None
            with _ ->
                None
