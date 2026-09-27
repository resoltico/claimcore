module ClaimCore.IntegrationTests.BackupCaptureSessionTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Database
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures

let private payload value = Encoding.ASCII.GetBytes(value + "\n")
let private nonce = String.replicate 64 "a"
let private digest = String.replicate 64 "b"
let private now = DateTimeOffset(2026, 9, 27, 4, 30, 0, TimeSpan.Zero)

let private beginFrame =
    payload (
        $"{{\"expiresAt\":\"2026-09-27T04:35:00Z\",\"format\":\"claimcore-backup-barrier-frame-1\",\"kind\":\"BEGIN\",\"nonce\":\"{nonce}\"}}"
    )

let private finishFrame leaseId root =
    payload (
        $"{{\"checkpointPath\":\"{root}/checkpoint.json\",\"cycleManifestPath\":\"{root}/cycle.json\",\"cycleManifestSignaturePath\":\"{root}/cycle.sig\",\"format\":\"claimcore-backup-barrier-frame-1\",\"kind\":\"FINISH\",\"leaseId\":\"{leaseId:D}\",\"nonce\":\"{nonce}\",\"primaryCiphertextPath\":\"{root}/primary.age\",\"witnessCiphertextPath\":\"{root}/witness.age\"}}"
    )

let private observeFrame receiptId =
    payload (
        $"{{\"cycleReceiptId\":\"{receiptId:D}\",\"format\":\"claimcore-backup-barrier-frame-1\",\"kind\":\"OBSERVE\",\"nonce\":\"{nonce}\",\"receiptSha256\":\"{digest}\"}}"
    )

let private privateRoot () =
    let temporary = Path.GetTempPath()

    let canonical =
        if
            OperatingSystem.IsMacOS()
            && temporary.StartsWith("/var/", StringComparison.Ordinal)
        then
            "/private" + temporary
        else
            temporary

    let path =
        Path.Combine(canonical, "claimcore-backup-session-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(path) |> ignore

    File.SetUnixFileMode(
        path,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )

    path

let private evidence root receiptId =
    { new IBackupCaptureEvidence with
        member _.Describe(cutoff, id, leaseId, checkedAt, expiresAt) =
            Some
                {
                    Nonce = id
                    LeaseId = leaseId
                    Cutoff = cutoff
                    PrimarySystemId = "1111111111111111111"
                    PrimaryTimeline = 1L
                    WitnessSystemId = "2222222222222222222"
                    WitnessTimeline = 1L
                    MaintenanceEvidenceSha256 = digest
                    CheckedAt = checkedAt
                    ValidUntil = expiresAt
                    CycleRoot = root
                }

        member _.Seal(held, _, _) =
            Task.FromResult(
                Some
                    {
                        Nonce = held.Nonce
                        LeaseId = held.LeaseId
                        CycleReceiptId = receiptId
                        WitnessSequence = held.Cutoff.WitnessSequence
                        WitnessHash = Convert.ToHexStringLower held.Cutoff.WitnessHash
                        ReceiptSha256 = digest
                    }
            )

        member _.Observe(_, _) = Task.FromResult true
    }

let private assertKind bytes kind message =
    use document = DatabaseRestoreCanonical.parse bytes |> Option.get
    Expect.equal (document.RootElement.GetProperty("kind").GetString()) kind message

let private exerciseSession (session: DatabaseBackupCaptureSession) root leaseId receiptId =
    let sealedBytes =
        session.Accept(finishFrame leaseId root, now.AddMinutes(1.), CancellationToken.None)
        |> await

    assertKind sealedBytes "SEALED" "Capture is sealed but not retained."

    let observed =
        session.Accept(observeFrame receiptId, now.AddMinutes(2.), CancellationToken.None)
        |> await

    assertKind observed "OBSERVED" "Exact receipt is read back."

    Expect.throws
        (fun () ->
            session.Accept(finishFrame leaseId root, now.AddMinutes(3.), CancellationToken.None)
            |> await
            |> ignore)
        "A consumed capture lease cannot finish twice."

let private run owner app _ (witness: WitnessProtocol) =
    let root = privateRoot ()

    try
        use connection = new NpgsqlConnection(owner)
        connection.Open()
        use source = RuntimeDataSource.create app
        let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
        let leaseId = Guid.NewGuid()
        let receiptId = Guid.NewGuid()

        let session, held =
            DatabaseBackupCaptureSession.Begin(
                connection,
                source,
                witness,
                commitments,
                evidence root receiptId,
                leaseId,
                beginFrame,
                now,
                CancellationToken.None
            )
            |> await

        use current = session :> IDisposable
        assertKind held "HELD" "Owner holds one lease."
        exerciseSession session root leaseId receiptId
    finally
        Directory.Delete(root)

let private exercised =
    testCase
        "[CC-BACKUP-001] owner backup session holds capture fence through sealing and releases before readback"
        (fun _ -> withAuthorityRuntimeDatabase run)

let tests = testList "owner backup capture session" [ exercised ]
