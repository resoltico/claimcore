module internal ClaimCore.IntegrationTests.BackupCaptureEvidenceFixture

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text.Json
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.HostSecurity

let private fields () =
    SortedDictionary<string, objnull>(StringComparer.Ordinal)

let private put (target: SortedDictionary<string, objnull>) name value = target.Add(name, box value)

let private sha (bytes: byte array) =
    SHA256.HashData(ReadOnlySpan<byte>(bytes)) |> Convert.ToHexStringLower

let private canonical (value: SortedDictionary<string, objnull>) =
    let bytes = Array.append (JsonSerializer.SerializeToUtf8Bytes value) [| byte '\n' |]

    match DatabaseRestoreCanonical.parse bytes with
    | Some document ->
        document.Dispose()
        bytes
    | None -> failwith "Synthetic backup capture claims are not canonical."

let private store path (bytes: byte array) =
    match PrivateFileService.writeNew 131072 path bytes with
    | Ok() -> ()
    | Error _ -> failwith "Synthetic private backup artifact could not be written."

let private identity
    (held: BackupCaptureHeld)
    (target: SortedDictionary<string, objnull>)
    (cycle: Guid)
    =
    put target "cycleId" (cycle.ToString("D"))
    put target "installationId" (held.Cutoff.InstallationId.ToString("D"))
    put target "lineageId" (held.Cutoff.LineageId.ToString("D"))
    put target "epoch" held.Cutoff.Epoch
    put target "leaseId" (held.LeaseId.ToString("D"))
    put target "captureNonce" held.Nonce
    put target "writerGeneration" held.Cutoff.WriterGeneration
    put target "backupCaptureSequence" held.Cutoff.WitnessSequence
    put target "backupCaptureHash" (Convert.ToHexStringLower held.Cutoff.WitnessHash)
    put target "maintenanceEvidenceSha256" held.MaintenanceEvidenceSha256
    put target "capturedAt" (held.CheckedAt.ToString("yyyy-MM-ddTHH:mm:ss'Z'"))

let private clusterMetadata (held: BackupCaptureHeld) cluster (ciphertext: byte array) =
    let result = fields ()

    let baseIdentity =
        [|
            held.Cutoff.InstallationId.ToString("D")
            held.Cutoff.LineageId.ToString("D")
            string held.Cutoff.Epoch
        |]

    let values =
        if cluster = "witness" then
            Array.append
                baseIdentity
                [|
                    string held.Cutoff.WitnessSequence
                    Convert.ToHexStringLower held.Cutoff.WitnessHash
                |]
        else
            baseIdentity

    put result "before" values
    put result "after" values
    put result "ciphertextSha256" (sha ciphertext)
    put result "ciphertextBytes" (int64 ciphertext.Length)
    result

let private baseCopy (held: BackupCaptureHeld) cluster (copyId: Guid) (ciphertext: byte array) =
    let result = fields ()

    let system, timeline =
        if cluster = "primary" then
            held.PrimarySystemId, held.PrimaryTimeline
        else
            held.WitnessSystemId, held.WitnessTimeline

    put result "copyId" (copyId.ToString("D"))
    put result "eventId" (Guid.NewGuid().ToString("D"))
    put result "postgresSystemId" system
    put result "timeline" timeline
    put result "walSegmentBytes" 16777216L
    put result "backupManifestSha256" (String.replicate 64 "a")
    put result "walStartLsn" "0/1000000"
    put result "walEndLsn" "0/2000000"
    put result "ciphertextSha256" (sha ciphertext)
    put result "ciphertextBytes" (int64 ciphertext.Length)
    put result "locationCommitment" (String.replicate 64 "b")
    put result "custodianCommitment" (String.replicate 64 "c")
    result

let private checkpoint (held: BackupCaptureHeld) (cycle: Guid) (checkpointKey: Guid) =
    let result = fields ()
    identity held result cycle
    put result "format" "claimcore-witness-checkpoint-1"
    put result "sequence" held.Cutoff.WitnessSequence
    put result "hash" (Convert.ToHexStringLower held.Cutoff.WitnessHash)
    put result "checkpointSigningKeyId" (checkpointKey.ToString("D"))
    put result "checkpointCustodianCommitment" (String.replicate 64 "d")
    canonical result

let private manifest
    (held: BackupCaptureHeld)
    (cycle: Guid)
    (copyKey: Guid)
    (checkpointKey: Guid)
    (checkpointBytes: byte array)
    (primary: byte array)
    (witness: byte array)
    =
    let result = fields ()
    identity held result cycle
    put result "format" "claimcore-backup-cycle-1"
    put result "consistencyScope" "unfenced-capture"
    put result "primary" (clusterMetadata held "primary" primary)
    put result "witness" (clusterMetadata held "witness" witness)
    let tip = fields ()
    put tip "sequence" held.Cutoff.WitnessSequence
    put tip "hash" (Convert.ToHexStringLower held.Cutoff.WitnessHash)
    put result "witnessCheckpoint" tip
    let primaryId, witnessId = Guid.NewGuid(), Guid.NewGuid()
    let copyIds = fields ()
    put copyIds "primary" (primaryId.ToString("D"))
    put copyIds "witness" (witnessId.ToString("D"))
    put result "copyIds" copyIds
    let copies = fields ()
    put copies "primary" (baseCopy held "primary" primaryId primary)
    put copies "witness" (baseCopy held "witness" witnessId witness)
    put result "baseCopies" copies
    put result "copySigningKeyId" (copyKey.ToString("D"))
    put result "checkpointSigningKeyId" (checkpointKey.ToString("D"))
    put result "checkpointSha256" (sha checkpointBytes)
    canonical result

let writeCapture
    (held: BackupCaptureHeld)
    checkpointRoot
    copyKeyId
    (copyKey: Key)
    checkpointKeyId
    (checkpointKey: Key)
    =
    let cycle = Guid.NewGuid()
    let primary = [| 1uy; 2uy; 3uy |]
    let witness = [| 4uy; 5uy; 6uy |]
    let checkpointBytes = checkpoint held cycle checkpointKeyId

    let manifestBytes =
        manifest held cycle copyKeyId checkpointKeyId checkpointBytes primary witness

    let checkpointPath = Path.Combine(checkpointRoot, cycle.ToString("D") + ".json")
    let manifestPath = Path.Combine(held.CycleRoot, "manifest.json")

    let files =
        {
            PrimaryCiphertextPath = Path.Combine(held.CycleRoot, "primary.tar.age")
            WitnessCiphertextPath = Path.Combine(held.CycleRoot, "witness.tar.age")
            CheckpointPath = checkpointPath
            CycleManifestPath = manifestPath
            CycleManifestSignaturePath = Path.Combine(held.CycleRoot, "manifest.sig")
        }

    store files.PrimaryCiphertextPath primary
    store files.WitnessCiphertextPath witness
    store checkpointPath checkpointBytes

    let checkpointSignaturePath =
        Path.ChangeExtension(checkpointPath, ".sig")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failwith "Synthetic checkpoint signature path is invalid.")

    store checkpointSignaturePath (SignatureAlgorithm.Ed25519.Sign(checkpointKey, checkpointBytes))
    store manifestPath manifestBytes
    store files.CycleManifestSignaturePath (SignatureAlgorithm.Ed25519.Sign(copyKey, manifestBytes))
    files
