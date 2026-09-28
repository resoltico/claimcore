module ClaimCore.IntegrationTests.BackupCapturePhysicalTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.BackupCapturePhysicalPrivate
open ClaimCore.IntegrationTests.BackupCapturePhysicalDocuments
open ClaimCore.IntegrationTests.BackupCapturePhysicalSigners
open ClaimCore.IntegrationTests.BackupCapturePhysicalProcess

let private countCopies owner =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand("SELECT count(*) FROM claimcore.managed_copies", connection)

    command.ExecuteScalar() :?> int64

let private cycleReceipt (paths: CapturePaths) (response: JsonElement) =
    let encoded = response.GetProperty("cycleReceiptId").GetString()

    match Guid.TryParseExact(encoded, "D") with
    | true, value when value <> Guid.Empty ->
        Path.Combine(paths.Archive, value.ToString("D"), "capture-receipt.json")
    | _ -> failtest "Owner capture receipt ID was unavailable."

let private positive (paths: CapturePaths) owner (runCapture: bool -> int * JsonElement) =
    let code, response = runCapture false

    if code <> 0 then
        let reason =
            match response.TryGetProperty("reason") with
            | true, value -> value.GetString() |> Option.ofObj |> Option.defaultValue "unavailable"
            | _ -> "unavailable"

        failtest ("Physical capture refused at safe stage " + reason)

    Expect.equal code 0 "Two real BASE streams should reach exact SEALED+OBSERVED."

    Expect.equal
        (response.GetProperty("status").GetString())
        "CAPTURED_UNVERIFIED"
        "Owner receipt does not retain a backup."

    Expect.isFalse
        (response.GetProperty("realDataReady").GetBoolean())
        "Same-Mac capture cannot admit real data."

    let receipt = cycleReceipt paths response
    Expect.isTrue (File.Exists receipt) "Owner receipt was durably read back."

    let cycle =
        Path.GetDirectoryName receipt
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Owner cycle directory is absent.")

    Expect.isTrue
        (File.Exists(Path.Combine(cycle, "primary.tar.age")))
        "Encrypted primary BASE exists."

    Expect.isTrue
        (File.Exists(Path.Combine(cycle, "witness.tar.age")))
        "Encrypted witness BASE exists."

    Expect.isFalse
        (File.Exists(Path.Combine(cycle, "primary.tar")))
        "Plaintext primary BASE was not retained."

    use document =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cycle, "manifest.json")))

    let manifest = document.RootElement

    Expect.equal
        (manifest.GetProperty("consistencyScope").GetString())
        "unfenced-capture"
        "Producer label is not a self-certified fence."

    Expect.equal (countCopies owner) 0L "Capture alone did not REGISTER or RETAIN a copy."

let private tampered (paths: CapturePaths) owner (runCapture: bool -> int * JsonElement) =
    let before = Directory.EnumerateDirectories(paths.Archive) |> Set.ofSeq
    let code, response = runCapture true
    Expect.notEqual code 0 "Changed encrypted PRIMARY must not be sealed."

    Expect.equal
        (response.GetProperty("status").GetString())
        "CAPTURE_UNCONFIRMED"
        "Post-FINISH uncertainty is preserved."

    Expect.isFalse
        (response.GetProperty("realDataReady").GetBoolean())
        "Changed copy never opens casework."

    let added = (Directory.EnumerateDirectories(paths.Archive) |> Set.ofSeq) - before
    Expect.equal added.Count 1 "One scoped changed cycle remains for owner reconciliation."

    for directory in added do
        Expect.isFalse
            (File.Exists(Path.Combine(directory, "capture-receipt.json")))
            "Changed bytes have no definite owner receipt."

    Expect.equal (countCopies owner) 0L "Changed capture did not write copy authority."

let private run owner app writer (witness: WitnessProtocol) =
    let paths = roots ()

    try
        let copy = keyPair paths "copy-attestor"
        let checkpoint = keyPair paths "checkpoint"
        let copyId, checkpointId = registered owner app writer witness copy checkpoint
        let selectedWitnessOwner = witnessOwnerFor writer
        prepareReplication owner selectedWitnessOwner
        writeServices paths owner selectedWitnessOwner
        let identity = witness.Identity

        writeConfiguration
            paths
            copy
            checkpoint
            copyId
            checkpointId
            identity.InstallationId
            identity.LineageId
            identity.Epoch

        withCapture paths owner app writer witness (fun execute ->
            positive paths owner execute
            tampered paths owner execute)

        use source = RuntimeDataSource.create app
        use audit = RuntimeDatabase.openConnection source
        DataAudit.run audit witness CancellationToken.None |> await |> ignore
    finally
        if Directory.Exists paths.SocketRoot then
            Directory.Delete(paths.SocketRoot, true)

        if Directory.Exists paths.Scratch then
            Directory.Delete(paths.Scratch, true)

let tests =
    testList
        "owner fenced physical backup capture"
        [
            testCase
                "[CC-BACKUP-001] real dual BASE capture seals exact owner receipt and changed ciphertext stays uncertain"
                (fun _ -> withAuthorityRuntimeDatabase run)
        ]
