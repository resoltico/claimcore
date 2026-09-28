module internal ClaimCore.IntegrationTests.BackupCapturePhysicalDocuments

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Expecto
open ClaimCore.IntegrationTests.FixtureEnvironment
open ClaimCore.IntegrationTests.RestorePhysicalProcess
open ClaimCore.IntegrationTests.BackupCapturePhysicalPrivate

let private cycleMaterial (paths: CapturePaths) =
    command "age-keygen" [ "-o"; paths.AgeIdentity ] "Age identity generation failed"
    |> ignore

    let recipient =
        command "age-keygen" [ "-y"; paths.AgeIdentity ] "Age recipient is unavailable"

    File.SetUnixFileMode(paths.AgeIdentity, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    if not (recipient.StartsWith("age1", StringComparison.Ordinal)) then
        failtest "Age recipient is invalid."

    let commitment = Path.Combine(paths.Scratch, "commitment.key")
    let material = RandomNumberGenerator.GetBytes 32

    try
        privateBytes paths.Scratch commitment material
    finally
        CryptographicOperations.ZeroMemory material

    recipient, commitment

let private signerDocument
    (paths: CapturePaths)
    (checkpoint: SigningKeyFiles)
    (checkpointKeyId: Guid)
    (installationId: Guid)
    (lineageId: Guid)
    epoch
    =
    let signer =
        {|
            format = "claimcore-checkpoint-signer-config-1"
            purpose = "CHECKPOINT"
            transport = "LOCAL_SOCKET"
            socketPath = paths.Socket
            ledgerRoot = paths.SignerLedger
            signingKeyFile = checkpoint.PrivatePath
            verificationKeyFile = checkpoint.PublicPath
            checkpointSigningKeyId = checkpointKeyId.ToString("D")
            installationId = installationId.ToString("D")
            lineageId = lineageId.ToString("D")
            epoch = epoch
        |}

    privateFile paths.Scratch paths.SignerConfigFile (JsonSerializer.Serialize signer)

let private cluster service custodian =
    {|
        metadataService = service
        replicationService = service + "_replication"
        custodianId = custodian
    |}

let private ownerDocument
    (paths: CapturePaths)
    (copy: SigningKeyFiles)
    (checkpoint: SigningKeyFiles)
    (copyKeyId: Guid)
    (checkpointKeyId: Guid)
    recipient
    commitment
    =
    let config =
        {|
            format = "claimcore-managed-backup-1"
            archiveRoot = paths.Archive
            checkpointRoot = paths.Checkpoint
            inventoryRoot = paths.Inventory
            commitmentKey = commitment
            signingKeyId = copyKeyId.ToString("D")
            checkpointSigningKeyId = checkpointKeyId.ToString("D")
            encryptionKeyId = Guid.NewGuid().ToString("D")
            backupIntervalSeconds = 86400
            maximumBackupAgeSeconds = 172800
            restoreHorizonSeconds = 259200
            backupRetentionSeconds = 604800
            walRetentionSeconds = 604800
            checkpointRetentionSeconds = 1209600
            ageRecipient = recipient
            ageIdentity = paths.AgeIdentity
            signingKey = copy.PrivatePath
            verificationKey = copy.PublicPath
            checkpointSignerMode = "LOCAL_SYNTHETIC"
            checkpointSignerSocket = paths.Socket
            checkpointSignerRemote = (null: objnull)
            checkpointVerificationKey = checkpoint.PublicPath
            checkpointCustodianId = "synthetic-checkpoint-custodian"
            pgServiceFile = paths.ServiceFile
            pgServiceFileSha256 =
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes paths.ServiceFile))
            pgTlsRootSha256 = (null: objnull)
            maxBackupBytes = 1073741824
            maxTarEntries = 100000
            maxWalCopies = 1000
            primary = cluster "backup_primary" "synthetic-primary"
            witness = cluster "backup_witness" "synthetic-witness"
        |}

    privateFile paths.Scratch paths.ConfigFile (JsonSerializer.Serialize config)

let writeConfiguration
    (paths: CapturePaths)
    (copy: SigningKeyFiles)
    (checkpoint: SigningKeyFiles)
    (copyKeyId: Guid)
    (checkpointKeyId: Guid)
    (installationId: Guid)
    (lineageId: Guid)
    epoch
    =
    let recipient, commitment = cycleMaterial paths
    signerDocument paths checkpoint checkpointKeyId installationId lineageId epoch
    ownerDocument paths copy checkpoint copyKeyId checkpointKeyId recipient commitment
