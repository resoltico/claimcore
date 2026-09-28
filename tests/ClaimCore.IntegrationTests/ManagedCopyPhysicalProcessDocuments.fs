module internal ClaimCore.IntegrationTests.ManagedCopyPhysicalProcessDocuments

open System
open System.IO
open System.Security.Cryptography
open System.Text
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.RestorePhysicalCopyFixture
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.Postgres
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type PhysicalCopyDescriptor =
    {
        Cluster: string
        Kind: string
        CiphertextPath: string
        ArchiveRoot: string
        AgeIdentityPath: string
        PostgresSystemId: string
        Timeline: int
        WalSegmentBytes: int
        BackupManifestSha256: string option
        WalStartLsn: string option
        WalEndLsn: string option
        WalSegment: string option
        CustodianId: string
        OwnerRole: string option
        DatabaseName: string option
    }

let fromArchive (capture: PhysicalCopyCapture) (item: PhysicalArchiveCopy) =
    let ownerRole, database =
        if item.Kind = "WAL" then
            None, None
        elif item.Cluster = "PRIMARY" then
            Some capture.OwnerRole, Some capture.DatabaseName
        else
            Some capture.WitnessOwnerRole, Some capture.WitnessDatabaseName

    {
        Cluster = item.Cluster
        Kind = item.Kind
        CiphertextPath = item.CiphertextPath
        ArchiveRoot = capture.ArchiveRoot
        AgeIdentityPath = capture.AgeIdentityPath
        PostgresSystemId = item.PostgresSystemId
        Timeline = int item.Timeline
        WalSegmentBytes = item.WalSegmentBytes
        BackupManifestSha256 = item.BackupManifestSha256
        WalStartLsn = item.WalStartLsn
        WalEndLsn = item.WalEndLsn
        WalSegment = item.WalSegment
        CustodianId =
            if item.Cluster = "PRIMARY" then
                "synthetic-primary-backup-custodian"
            else
                "synthetic-witness-backup-custodian"
        OwnerRole = ownerRole
        DatabaseName = database
    }

let primaryBase (capture: PhysicalCopyCapture) =
    capture.Objects
    |> List.find (fun item -> item.Cluster = "PRIMARY" && item.Kind = "BASE")
    |> fromArchive capture

let private shaFile path =
    use source = File.OpenRead(path)
    SHA256.HashData(source) |> Convert.ToHexStringLower

let private location key (path: string) =
    HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("location\000" + path))
    |> Convert.ToHexStringLower

let private custodian key value =
    HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("custodian\000" + value))
    |> Convert.ToHexStringLower

let private optionalField value =
    value |> Option.map element |> Option.defaultValue nil

let registration
    owner
    (copy: PhysicalCopyDescriptor)
    (captureTip: Snapshot)
    (copySignerKeyId: Guid)
    (copyEventId: Guid)
    (copyId: Guid)
    (commitmentKey: byte array)
    =
    let captured = DateTimeOffset(File.GetLastWriteTimeUtc(copy.CiphertextPath))

    registerBase owner captureTip copySignerKeyId copyEventId copyId
    |> fun source ->
        use document = System.Text.Json.JsonDocument.Parse(source)

        let cycleId =
            if copy.Kind = "BASE" then
                document.RootElement.GetProperty("cycleId").Clone()
            else
                nil

        changed
            source
            [
                "capturedAt", element (stamp captured)
                "retainUntil", element (stamp (captured.AddDays(7.)))
                "cluster", element copy.Cluster
                "kind", element copy.Kind
                "postgresSystemId", element copy.PostgresSystemId
                "timeline", element copy.Timeline
                "walSegmentBytes", element copy.WalSegmentBytes
                "backupManifestSha256", optionalField copy.BackupManifestSha256
                "walStartLsn", optionalField copy.WalStartLsn
                "walEndLsn", optionalField copy.WalEndLsn
                "walSegment", optionalField copy.WalSegment
                "cycleId", cycleId
                "ciphertextSha256", element (shaFile copy.CiphertextPath)
                "ciphertextBytes", element (FileInfo(copy.CiphertextPath).Length)
                "locationCommitment", element (location commitmentKey copy.CiphertextPath)
                "custodianCommitment", element (custodian commitmentKey copy.CustodianId)
            ]

let private verifierIdentityFields
    (copy: PhysicalCopyDescriptor)
    (registration: ManagedCopyAttestation)
    (tip: Snapshot)
    (eventId: Guid)
    =
    [
        "format", element "claimcore-managed-copy-verification-input-1"
        "syntheticTestOverride", element true
        "verificationEventId", element (eventId.ToString("D"))
        "copyId", element (registration.CopyId.ToString("D"))
        "copyEventId", element (registration.EventId.ToString("D"))
        "copyRevision", element 2
        "archiveObjectId", element (Guid.NewGuid().ToString("D"))
        "installationId", element (tip.Identity.InstallationId.ToString("D"))
        "lineageId", element (tip.Identity.LineageId.ToString("D"))
        "witnessEpoch", element tip.Identity.Epoch
        "witnessCutoffSequence", element tip.TipSequence
        "witnessCutoffHash", element (Convert.ToHexStringLower(tip.TipHash))
        "cluster", element copy.Cluster
        "kind", element copy.Kind
        "postgresSystemId", element registration.PostgresSystemId.Value
        "timeline", element registration.Timeline.Value
        "walSegmentBytes", element registration.WalSegmentBytes.Value
        "backupManifestSha256", optionalField copy.BackupManifestSha256
        "walStartLsn", optionalField copy.WalStartLsn
        "walEndLsn", optionalField copy.WalEndLsn
        "walSegment", optionalField copy.WalSegment
    ]

let private verifierStorageFields
    (capture: PhysicalCopyCapture)
    (copy: PhysicalCopyDescriptor)
    (verifierKeyId: Guid)
    (verifierHolder: Guid)
    (proofPath: string)
    (signaturePath: string)
    (commitmentKey: byte array)
    =
    let objectPath = copy.CiphertextPath

    [
        "locationCommitment", element (location commitmentKey objectPath)
        "ciphertextSha256", element (shaFile objectPath)
        "ciphertextBytes", element (FileInfo(objectPath).Length)
        "ciphertextFile", element objectPath
        "archiveRoot", element copy.ArchiveRoot
        "ageIdentityFile", element copy.AgeIdentityPath
        "verificationSigningKeyFile", element capture.SigningKeyPath
        "verifierSigningKeyId", element (verifierKeyId.ToString("D"))
        "verifierHolderActorId", element (verifierHolder.ToString("D"))
        "outputProofFile", element proofPath
        "outputSignatureFile", element signaturePath
        "privateScratchRoot",
        element (
            Path.GetDirectoryName(proofPath)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Private proof root is absent.")
        )
        "maximumPlaintextBytes", element 1073741824
        "maximumTarEntries", element 100000
        "databaseOwnerRole", optionalField copy.OwnerRole
        "databaseName", optionalField copy.DatabaseName
    ]

let verifierOverride
    (capture: PhysicalCopyCapture)
    (copy: PhysicalCopyDescriptor)
    (registration: ManagedCopyAttestation)
    (tip: Snapshot)
    (verifierKeyId: Guid)
    (verifierHolder: Guid)
    (eventId: Guid)
    (proofPath: string)
    (signaturePath: string)
    (commitmentKey: byte array)
    =
    canonical (
        verifierIdentityFields copy registration tip eventId
        @ verifierStorageFields
            capture
            copy
            verifierKeyId
            verifierHolder
            proofPath
            signaturePath
            commitmentKey
    )

let ownerInput (copyId: Guid) objectPath proofPath signaturePath keyPath maximum =
    canonical
        [
            "format", element "claimcore-managed-copy-physical-input-1"
            "copyId", element (copyId.ToString("D"))
            "objectPath", element objectPath
            "maximumObjectBytes", element maximum
            "proofFile", element proofPath
            "signatureFile", element signaturePath
            "commitmentKeyFile", element keyPath
        ]
