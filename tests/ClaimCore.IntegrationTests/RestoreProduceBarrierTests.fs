module ClaimCore.IntegrationTests.RestoreProduceBarrierTests

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Expecto
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.IntegrationTests.RestoreProduceCanonicalTests

let private privateDirectory action =
    let temporary = Path.GetTempPath()

    let physical =
        if
            OperatingSystem.IsMacOS()
            && (temporary.StartsWith("/var/", StringComparison.Ordinal)
                || temporary.StartsWith("/tmp/", StringComparison.Ordinal))
        then
            "/private" + temporary
        else
            temporary

    let root =
        Path.Combine(physical, "claimcore-barrier-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(
        root,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )
    |> ignore

    try
        action root
    finally
        Directory.Delete(root, true)

let private barrier (report: RestoreReportClaims) (index: RestoreEvidenceIndex) =
    let fields = SortedDictionary<string, objnull>(StringComparer.Ordinal)
    let put name value = fields.Add(name, box value)
    put "format" "claimcore-quiescent-audit-barrier-1"
    put "cycleId" (report.CycleId.ToString("D"))
    put "installationId" (report.InstallationId.ToString("D"))
    put "lineageId" (report.LineageId.ToString("D"))
    put "epoch" report.Epoch
    put "backupCaptureSequence" report.BackupCaptureSequence
    put "backupCaptureHash" report.BackupCaptureHash
    put "witnessCutoff" report.WitnessCutoff
    put "witnessCutoffHash" report.WitnessCutoffHash
    put "primaryCaptureWalEndpoint" index.PrimaryCaptureWalEndpoint
    put "witnessCaptureWalEndpoint" index.WitnessCaptureWalEndpoint
    put "primaryRegisteredWalHorizon" index.PrimaryRegisteredWalHorizon
    put "witnessRegisteredWalHorizon" index.WitnessRegisteredWalHorizon
    put "primaryWalEndpoint" index.PrimaryWalEndpoint
    put "witnessWalEndpoint" index.WitnessWalEndpoint
    put "writerPaused" true
    put "admittedMutationsDrained" true
    put "primarySnapshotStable" true
    put "witnessCutoffStable" true
    put "recoveryTailUnsealed" true
    fields

let private bytes (fields: SortedDictionary<string, objnull>) =
    Encoding.ASCII.GetBytes(JsonSerializer.Serialize(fields) + "\n")

let private write path value =
    match PrivateFileService.writeNew 32768 path value with
    | Ok() -> ()
    | Error _ -> failtest "Synthetic signed barrier file was refused"

let private check root report index (key: Key) fields name =
    let encoded = bytes fields
    let signature = SignatureAlgorithm.Ed25519.Sign(key, encoded)
    let file = Path.Combine(root, name + ".json")
    let detached = Path.Combine(root, name + ".sig")
    write file encoded
    write detached signature

    let digest =
        SHA256.HashData(ReadOnlySpan<byte>(encoded)) |> Convert.ToHexStringLower

    let candidate =
        { report with
            QuiescentBarrierSha256 = digest
        }

    let paths =
        { index with
            BarrierFile = file
            BarrierSignatureFile = detached
        }

    let publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey)
    candidate, paths, publicKey

let private exactPhase =
    testCase
        "[CC-BACKUP-001] signed audit barrier refuses future cutover flag substitution"
        (fun _ ->
            if not (OperatingSystem.IsWindows()) then
                privateDirectory (fun root ->
                    let report, index, _ = specimen ()
                    use key = Key.Create(SignatureAlgorithm.Ed25519)
                    let fields = barrier report index
                    let accepted, paths, publicKey = check root report index key fields "audit"
                    DatabaseRestoreBarrier.verify paths accepted publicKey
                    fields.Remove("writerPaused") |> ignore
                    fields.Add("writerFenceVerified", box true)
                    let wrong, wrongPaths, _ = check root report index key fields "cutover"

                    Expect.throws
                        (fun () -> DatabaseRestoreBarrier.verify wrongPaths wrong publicKey)
                        "A permanent fence claim cannot replace a paused audit writer"))

let tests = testList "restore audit barrier" [ exactPhase ]
