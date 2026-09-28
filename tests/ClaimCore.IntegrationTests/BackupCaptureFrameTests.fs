module ClaimCore.IntegrationTests.BackupCaptureFrameTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Database

let private bytes value = Encoding.ASCII.GetBytes(value + "\n")
let private nonce = String.replicate 64 "a"
let private now = DateTimeOffset(2026, 9, 27, 4, 30, 0, TimeSpan.Zero)
let private leaseId = Guid.Parse("11111111-1111-4111-8111-111111111111")

let private beginFrame =
    bytes (
        $"{{\"expiresAt\":\"2026-09-27T04:35:00Z\",\"format\":\"claimcore-backup-barrier-frame-1\",\"kind\":\"BEGIN\",\"nonce\":\"{nonce}\"}}"
    )

let private finishFrame primary witness =
    bytes (
        $"{{\"checkpointPath\":\"/private/checkpoint.json\",\"cycleManifestPath\":\"/private/cycle.json\",\"cycleManifestSignaturePath\":\"/private/cycle.sig\",\"format\":\"claimcore-backup-barrier-frame-1\",\"kind\":\"FINISH\",\"leaseId\":\"{leaseId:D}\",\"nonce\":\"{nonce}\",\"primaryCiphertextPath\":\"{primary}\",\"witnessCiphertextPath\":\"{witness}\"}}"
    )

let private exactPrivateFrames =
    testCase "[CC-BACKUP-001] owner backup pipe admits only exact bounded private frames" (fun _ ->
        match DatabaseBackupCaptureFrames.parse beginFrame now with
        | BackupCaptureFrame.Begin(value, expires) ->
            Expect.equal value nonce "BEGIN binds exact nonce."
            Expect.equal expires (now.AddMinutes 5.) "BEGIN has bounded expiry."
        | _ -> failtest "Canonical BEGIN frame was not admitted."

        match
            DatabaseBackupCaptureFrames.parse
                (finishFrame "/private/primary.age" "/private/witness.age")
                now
        with
        | BackupCaptureFrame.Finish(value, id, files) ->
            Expect.equal value nonce "FINISH binds the same nonce."
            Expect.equal id leaseId "FINISH binds the owner lease."
            Expect.equal files.PrimaryCiphertextPath "/private/primary.age" "Primary is private."
        | _ -> failtest "Canonical FINISH frame was not admitted."

        for invalid in
            [
                finishFrame "/private/same.age" "/private/same.age"
                finishFrame "relative.age" "/private/witness.age"
                finishFrame "/private/../other.age" "/private/witness.age"
                bytes (
                    $"{{\"expiresAt\":\"2026-09-27T04:35:00Z\",\"extra\":true,\"format\":\"claimcore-backup-barrier-frame-1\",\"kind\":\"BEGIN\",\"nonce\":\"{nonce}\"}}"
                )
            ] do
            Expect.throws
                (fun () -> DatabaseBackupCaptureFrames.parse invalid now |> ignore)
                "Malformed or overlapping private frame refuses before owner action.")

let private boundedDelivery =
    testCase "[CC-BACKUP-001] private backup pipe frames are bounded and complete" (fun _ ->
        use inbound = new MemoryStream(Array.concat [ beginFrame; beginFrame ])

        Expect.sequenceEqual
            (DatabaseBackupControlPipe.readFrame inbound |> Option.get)
            beginFrame
            "One complete private frame is read."

        Expect.sequenceEqual
            (DatabaseBackupControlPipe.readFrame inbound |> Option.get)
            beginFrame
            "A second frame is not merged with the first."

        Expect.isNone (DatabaseBackupControlPipe.readFrame inbound) "Clean EOF ends the pipe."
        use truncated = new MemoryStream(beginFrame[.. beginFrame.Length - 2])

        Expect.throws
            (fun () -> DatabaseBackupControlPipe.readFrame truncated |> ignore)
            "An interrupted private frame refuses."

        use oversized = new MemoryStream(Array.create 16385 (byte 'x'))

        Expect.throws
            (fun () -> DatabaseBackupControlPipe.readFrame oversized |> ignore)
            "An oversized private frame refuses."

        use outbound = new MemoryStream()
        DatabaseBackupControlPipe.writeFrame outbound beginFrame
        Expect.sequenceEqual (outbound.ToArray()) beginFrame "Output keeps exact canonical bytes."

        Expect.throws
            (fun () ->
                DatabaseBackupControlPipe.allowCaptureUntil outbound (now.AddMinutes 5.) now)
            "A control stream without a bounded read timeout cannot hold authority.")

let private heldValue () : BackupCaptureHeld =
    {
        Nonce = nonce
        LeaseId = leaseId
        Cutoff =
            {
                InstallationId = Guid.Parse("22222222-2222-4222-8222-222222222222")
                LineageId = Guid.Parse("33333333-3333-4333-8333-333333333333")
                Epoch = 1L
                WriterGeneration = 1L
                WitnessSequence = 12L
                WitnessHash = Array.create 32 0xaauy
                AuthorityEvents = 2L
                AcceptedOperations = 1L
            }
        PrimarySystemId = "1111111111111111111"
        PrimaryTimeline = 1L
        WitnessSystemId = "2222222222222222222"
        WitnessTimeline = 1L
        MaintenanceEvidenceSha256 = String.replicate 64 "b"
        CheckedAt = now
        ValidUntil = now.AddMinutes(5.)
        CycleRoot = "/private/cycle"
    }

let private receiptValue () : BackupCaptureReceipt =
    {
        Nonce = nonce
        LeaseId = leaseId
        CycleReceiptId = Guid.Parse("44444444-4444-4444-8444-444444444444")
        WitnessSequence = 12L
        WitnessHash = String.replicate 64 "a"
        ReceiptSha256 = String.replicate 64 "c"
    }

let private ownerResponses =
    testCase "[CC-BACKUP-001] owner backup replies remain canonical and non-retained" (fun _ ->
        let held = heldValue ()

        let heldBytes = DatabaseBackupCaptureResponses.held held
        use document = DatabaseRestoreCanonical.parse heldBytes |> Option.get
        let root = document.RootElement
        Expect.equal (root.GetProperty("kind").GetString()) "HELD" "Owner holds a capture lease."
        Expect.equal (root.GetProperty("cutoffSequence").GetInt64()) 12L "Cutoff is exact."

        let receipt = receiptValue ()

        for kind in [ "SEALED"; "OBSERVED" ] do
            let encoded = DatabaseBackupCaptureResponses.receipt kind receipt
            use result = DatabaseRestoreCanonical.parse encoded |> Option.get

            Expect.equal
                (result.RootElement.GetProperty("kind").GetString())
                kind
                "Receipt is exact."

            let mutable retained = Unchecked.defaultof<JsonElement>

            Expect.isFalse
                (result.RootElement.TryGetProperty("retained", &retained))
                "A physical capture receipt makes no retention claim.")

let tests =
    testList "owner backup capture frames" [ exactPrivateFrames; boundedDelivery; ownerResponses ]
