module internal ClaimCore.IntegrationTests.RestoreProduceSourceEvidence

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Expecto
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Witness

let private fields () =
    SortedDictionary<string, objnull>(StringComparer.Ordinal)

let private put (values: SortedDictionary<string, objnull>) name value = values.Add(name, box value)

let private canonical (values: SortedDictionary<string, objnull>) =
    let encoded = JsonSerializer.SerializeToUtf8Bytes(values)
    let bytes = Array.append encoded [| byte '\n' |]

    match DatabaseRestoreCanonical.parse bytes with
    | Some document ->
        document.Dispose()
        bytes
    | None -> failtest "Synthetic signed source is not canonical ASCII JSON"

let sha (bytes: byte array) =
    SHA256.HashData(ReadOnlySpan<byte>(bytes)) |> Convert.ToHexStringLower

let writeSigned root name (bytes: byte array) (key: Key) =
    let path = Path.Combine(root, name + ".json")
    let signature = Path.Combine(root, name + ".sig")
    let signed = SignatureAlgorithm.Ed25519.Sign(key, bytes)

    match PrivateFileService.writeNew 131072 path bytes with
    | Ok() -> ()
    | Error _ -> failtest "Synthetic signed source file could not be created"

    match PrivateFileService.writeNew 64 signature signed with
    | Ok() -> ()
    | Error _ -> failtest "Synthetic source signature could not be created"

    path, signature, sha bytes

let directory parent name =
    let path = Path.Combine(parent, name)

    let mode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    Directory.CreateDirectory(path, mode) |> ignore
    path

let private identity values (facts: RestoredPairFacts) (cycle: Guid) =
    put values "installationId" (facts.InstallationId.ToString("D"))
    put values "lineageId" (facts.LineageId.ToString("D"))
    put values "epoch" facts.Epoch
    put values "cycleId" (cycle.ToString("D"))

let manifest facts cycle (captureTip: Snapshot) (primaryCopy: Guid) (witnessCopy: Guid) =
    let values = fields ()
    identity values facts cycle
    put values "format" "claimcore-backup-cycle-1"
    put values "consistencyScope" "unfenced-capture"
    let checkpoint = fields ()
    put checkpoint "sequence" captureTip.TipSequence
    put checkpoint "hash" (Convert.ToHexStringLower captureTip.TipHash)
    put values "witnessCheckpoint" checkpoint
    let copies = fields ()
    put copies "primary" (primaryCopy.ToString("D"))
    put copies "witness" (witnessCopy.ToString("D"))
    put values "copyIds" copies
    canonical values

let checkpoint facts cycle capturedAt =
    let values = fields ()
    identity values facts cycle
    put values "format" "claimcore-witness-checkpoint-1"
    put values "sequence" facts.WitnessCutoff
    put values "hash" facts.WitnessCutoffHash
    put values "capturedAt" capturedAt
    canonical values

let inventory facts cycle =
    let values = fields ()
    identity values facts cycle
    put values "format" "claimcore-managed-inventory-snapshot-1"
    put values "witnessCutoff" facts.WitnessCutoff
    put values "witnessCutoffHash" facts.WitnessCutoffHash
    put values "snapshotSha256" facts.ManagedCopySnapshotSha256
    put values "managedCopyCount" facts.ManagedCopyCount
    canonical values

let barrier
    facts
    cycle
    captureSequence
    captureHash
    primaryCapture
    witnessCapture
    primaryHorizon
    witnessHorizon
    =
    let values = fields ()
    identity values facts cycle
    put values "format" "claimcore-quiescent-audit-barrier-1"
    put values "backupCaptureSequence" captureSequence
    put values "backupCaptureHash" captureHash
    put values "witnessCutoff" facts.WitnessCutoff
    put values "witnessCutoffHash" facts.WitnessCutoffHash
    put values "primaryCaptureWalEndpoint" primaryCapture
    put values "witnessCaptureWalEndpoint" witnessCapture
    put values "primaryRegisteredWalHorizon" primaryHorizon
    put values "witnessRegisteredWalHorizon" witnessHorizon
    put values "primaryWalEndpoint" facts.PrimaryWalEndpoint
    put values "witnessWalEndpoint" facts.WitnessWalEndpoint

    for name in
        [
            "writerPaused"
            "admittedMutationsDrained"
            "primarySnapshotStable"
            "witnessCutoffStable"
            "recoveryTailUnsealed"
        ] do
        put values name true

    canonical values
