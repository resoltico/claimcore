module internal ClaimCore.IntegrationTests.BackupHealthSourceMaterial

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text.Json
open NSec.Cryptography
open ClaimCore.HostSecurity

let fields () =
    SortedDictionary<string, objnull>(StringComparer.Ordinal)

let put (value: SortedDictionary<string, objnull>) name item = value.Add(name, box item)

let canonical value =
    Array.append (JsonSerializer.SerializeToUtf8Bytes(value)) [| byte '\n' |]

let sha (bytes: byte array) =
    SHA256.HashData(bytes) |> Convert.ToHexStringLower

let stamp (value: DateTimeOffset) =
    value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")

let root () =
    let temporary = Path.GetTempPath()

    let physical =
        if
            OperatingSystem.IsMacOS()
            && temporary.StartsWith("/var/", StringComparison.Ordinal)
        then
            "/private" + temporary
        else
            temporary

    let path =
        Path.Combine(physical, "claimcore-health-source-" + Guid.NewGuid().ToString("N"))

    let mode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    Directory.CreateDirectory(path, mode) |> ignore
    path

let directory parent name =
    let mode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    let path = Path.Combine(parent, name)
    Directory.CreateDirectory(path, mode) |> ignore
    Directory.CreateDirectory(Path.Combine(path, "objects"), mode) |> ignore
    path

let objectFile root name (bytes: byte array) =
    let relative = "objects/" + name
    let path = Path.Combine(root, relative)

    match PrivateFileService.writeNew 1024 path bytes with
    | Ok() -> relative, sha bytes, int64 bytes.Length
    | Error _ -> invalidOp "Synthetic health object could not be created."

let role name marker (key: Key) =
    let value = fields ()
    put value "role" name

    put
        value
        "publicKeyBase64"
        (key.PublicKey.Export(KeyBlobFormat.RawPublicKey) |> Convert.ToBase64String)

    put value "machineSha256" (String(marker, 64))
    put value "storageSha256" (String(char (int marker + 3), 64))
    put value "adminActorId" (Guid.NewGuid().ToString("D"))
    put value "signerHolderActorId" (Guid.NewGuid().ToString("D"))
    value

let policyBytes archive checkpoint restore (keys: Key list) =
    let value = fields ()
    put value "format" "claimcore-backup-health-policy-1"
    put value "policyId" "reviewed-test-recovery"
    put value "backupIntervalSeconds" 300
    put value "maximumBackupAgeSeconds" 3600
    put value "maximumWalLagSeconds" 3600
    put value "maximumCheckpointAgeSeconds" 3600
    put value "maximumRestoreTestAgeSeconds" 3600
    put value "restoreHorizonSeconds" 7200
    put value "archiveRoot" archive
    put value "checkpointRoot" checkpoint
    put value "restoreRoot" restore

    put
        value
        "roles"
        [|
            role "archive" '1' keys[0]
            role "checkpoint" '2' keys[1]
            role "test-restore" '3' keys[2]
        |]

    canonical value

let archiveObject
    root
    (name: string)
    cluster
    kind
    system
    (segment: string option)
    (now: DateTimeOffset)
    =
    let copyId = Guid.NewGuid()

    let relative, digest, length =
        objectFile root (name + ".age") [| byte name.Length |]

    let value = fields ()
    put value "copyId" (copyId.ToString("D"))
    put value "revision" 2
    put value "cluster" cluster
    put value "kind" kind
    put value "postgresSystemId" system
    put value "timeline" 1
    put value "walSegmentBytes" 16777216
    put value "walHorizon" (if kind = "BASE" then "0/0" else "0/100")

    match segment with
    | None -> put value "walSegment" null
    | Some actual -> put value "walSegment" actual

    put value "ciphertextSha256" digest
    put value "ciphertextBytes" length
    put value "physicalReceiptSha256" (String((if cluster = "PRIMARY" then 'a' else 'b'), 64))
    put value "relativePath" relative
    put value "verifiedAt" (stamp (now.AddSeconds(-5.)))
    value, copyId

let checkpointObject root (cycleId: Guid) (now: DateTimeOffset) =
    let relative = cycleId.ToString("D") + ".json"

    match PrivateFileService.writeNew 1024 (Path.Combine(root, relative)) [| 9uy |] with
    | Ok() -> ()
    | Error _ -> invalidOp "Synthetic checkpoint could not be created."

    let digest, length = sha [| 9uy |], 1L
    let value = fields ()
    put value "sequence" 18
    put value "hash" (String('d', 64))
    put value "objectSha256" digest
    put value "objectBytes" length
    put value "relativePath" relative
    put value "verifiedAt" (stamp (now.AddSeconds(-5.)))
    value, digest

let restoreObject (primaryId: Guid) (witnessId: Guid) reportSha auditSha (now: DateTimeOffset) =
    let value = fields ()
    put value "reportSha256" reportSha
    put value "reportRelativePath" "objects/report.json"
    put value "fullAuditSha256" auditSha
    put value "auditRelativePath" "objects/audit.json"
    put value "witnessCutoff" 17
    put value "witnessCutoffHash" (String('e', 64))
    put value "primaryBaseCopyId" (primaryId.ToString("D"))
    put value "witnessBaseCopyId" (witnessId.ToString("D"))
    put value "primaryWalHorizon" "0/100"
    put value "witnessWalHorizon" "0/100"
    put value "primarySystemId" "1111111111111111111"
    put value "primaryTimeline" 1
    put value "witnessSystemId" "2222222222222222222"
    put value "witnessTimeline" 1
    put value "verifiedAt" (stamp (now.AddSeconds(-5.)))
    value

let sourceBytes
    (identity: Guid * Guid)
    (cycleId: Guid)
    (leaseId: Guid)
    (now: DateTimeOffset)
    objects
    checkpoint
    restored
    =
    let installation, lineage = identity
    let value = fields ()
    put value "format" "claimcore-backup-health-source-1"
    put value "cycleId" (cycleId.ToString("D"))
    put value "leaseId" (leaseId.ToString("D"))
    put value "captureNonce" (String('9', 64))
    put value "captureReceiptSha256" (String('8', 64))
    put value "backupCaptureSequence" 18
    put value "backupCaptureHash" (String('d', 64))
    put value "installationId" (installation.ToString("D"))
    put value "lineageId" (lineage.ToString("D"))
    put value "epoch" 1
    put value "writerGeneration" 1
    put value "policyId" "reviewed-test-recovery"
    put value "authorityRevision" 7
    put value "witnessTipSequence" 20
    put value "witnessTipHash" (String('c', 64))
    put value "knownCopyInventorySha256" (String('a', 64))
    put value "artifactCutoffSequence" 16
    put value "checkedAt" (stamp now)
    put value "validUntil" (stamp (now.AddMinutes(2.)))
    put value "objects" objects
    put value "checkpoint" checkpoint
    put value "testRestore" restored
    canonical value
