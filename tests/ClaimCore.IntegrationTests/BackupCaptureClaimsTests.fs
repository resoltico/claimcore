module ClaimCore.IntegrationTests.BackupCaptureClaimsTests

open System
open System.Collections.Generic
open System.Text.Json
open Expecto
open ClaimCore.Database

let private entry () =
    SortedDictionary<string, objnull>(StringComparer.Ordinal)

let private put (target: SortedDictionary<string, objnull>) name value = target.Add(name, box value)
let private digest character = String.replicate 64 (string character)
let private installation = Guid.Parse "11111111-1111-4111-8111-111111111111"
let private lineage = Guid.Parse "22222222-2222-4222-8222-222222222222"
let private cycle = Guid.Parse "33333333-3333-4333-8333-333333333333"
let private copyKey = Guid.Parse "44444444-4444-4444-8444-444444444444"
let private checkpointKey = Guid.Parse "55555555-5555-4555-8555-555555555555"
let private primaryCopy = Guid.Parse "66666666-6666-4666-8666-666666666666"
let private witnessCopy = Guid.Parse "77777777-7777-4777-8777-777777777777"
let private start = DateTimeOffset(2026, 9, 27, 5, 30, 0, TimeSpan.Zero)

let private held =
    {
        Nonce = digest 'a'
        LeaseId = Guid.Parse "88888888-8888-4888-8888-888888888888"
        Cutoff =
            {
                InstallationId = installation
                LineageId = lineage
                Epoch = 1L
                WriterGeneration = 2L
                WitnessSequence = 7L
                WitnessHash = Convert.FromHexString(digest 'b')
                AuthorityEvents = 3L
                AcceptedOperations = 4L
            }
        PrimarySystemId = "1111111111111111111"
        PrimaryTimeline = 1L
        WitnessSystemId = "2222222222222222222"
        WitnessTimeline = 2L
        MaintenanceEvidenceSha256 = digest 'c'
        CheckedAt = start
        ValidUntil = start.AddMinutes(30.)
        CycleRoot = "/synthetic/cycle"
    }

let private file bytes sha = { Bytes = bytes; Sha256 = digest sha }

let private files =
    {
        Primary = file 100L 'd'
        Witness = file 200L 'e'
        Checkpoint = file 300L 'f'
        CheckpointSignature = file 64L '1'
        Manifest = file 400L '2'
        ManifestSignature = file 64L '3'
    }

let private common (target: SortedDictionary<string, objnull>) =
    put target "cycleId" (cycle.ToString("D"))
    put target "installationId" (installation.ToString("D"))
    put target "lineageId" (lineage.ToString("D"))
    put target "epoch" 1L
    put target "leaseId" (held.LeaseId.ToString("D"))
    put target "captureNonce" held.Nonce
    put target "writerGeneration" 2L
    put target "backupCaptureSequence" 7L
    put target "backupCaptureHash" (digest 'b')
    put target "maintenanceEvidenceSha256" (digest 'c')
    put target "capturedAt" "2026-09-27T05:30:00Z"

let private baseCopy (copyId: Guid) system timeline (cipher: BackupCaptureFileDigest) =
    let result = entry ()
    put result "copyId" (copyId.ToString("D"))
    put result "eventId" (Guid.NewGuid().ToString("D"))
    put result "postgresSystemId" system
    put result "timeline" timeline
    put result "walSegmentBytes" 16777216L
    put result "backupManifestSha256" (digest '4')
    put result "walStartLsn" "0/1000000"
    put result "walEndLsn" "0/2000000"
    put result "ciphertextSha256" cipher.Sha256
    put result "ciphertextBytes" cipher.Bytes
    put result "locationCommitment" (digest '5')
    put result "custodianCommitment" (digest '6')
    result

let private metadata cluster (cipher: BackupCaptureFileDigest) =
    let result = entry ()
    let identity = [| installation.ToString("D"); lineage.ToString("D"); "1" |]

    let values =
        if cluster = "witness" then
            Array.append identity [| "7"; digest 'b' |]
        else
            identity

    put result "before" values
    put result "after" values
    put result "ciphertextSha256" cipher.Sha256
    put result "ciphertextBytes" cipher.Bytes
    result

let private source () =
    let manifest = entry ()
    common manifest
    put manifest "format" "claimcore-backup-cycle-1"
    put manifest "consistencyScope" "unfenced-capture"
    put manifest "primary" (metadata "primary" files.Primary)
    put manifest "witness" (metadata "witness" files.Witness)
    let tip = entry ()
    put tip "sequence" 7L
    put tip "hash" (digest 'b')
    put manifest "witnessCheckpoint" tip
    let copyIds = entry ()
    put copyIds "primary" (primaryCopy.ToString("D"))
    put copyIds "witness" (witnessCopy.ToString("D"))
    put manifest "copyIds" copyIds
    let copies = entry ()
    put copies "primary" (baseCopy primaryCopy held.PrimarySystemId 1L files.Primary)
    put copies "witness" (baseCopy witnessCopy held.WitnessSystemId 2L files.Witness)
    put manifest "baseCopies" copies
    put manifest "copySigningKeyId" (copyKey.ToString("D"))
    put manifest "checkpointSigningKeyId" (checkpointKey.ToString("D"))
    put manifest "checkpointSha256" files.Checkpoint.Sha256
    let checkpoint = entry ()
    common checkpoint
    put checkpoint "format" "claimcore-witness-checkpoint-1"
    put checkpoint "sequence" 7L
    put checkpoint "hash" (digest 'b')
    put checkpoint "checkpointSigningKeyId" (checkpointKey.ToString("D"))
    put checkpoint "checkpointCustodianCommitment" (digest '7')
    manifest, checkpoint

let private verify manifest checkpoint =
    use first = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes manifest)
    use second = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes checkpoint)
    DatabaseBackupCaptureClaims.verify held files first.RootElement second.RootElement

let private signedCaptureClaims =
    testCase "[CC-BACKUP-001] signed capture binds owner cutoff and both physical copies" (fun _ ->
        let manifest, checkpoint = source ()
        let accepted = verify manifest checkpoint
        Expect.equal accepted.CycleId cycle "Exact cycle ID is retained."

        Expect.equal
            accepted.CheckpointSigningKeyId
            checkpointKey
            "Independent signer purpose is explicit."

        checkpoint["hash"] <- box (digest '8')

        Expect.throws
            (fun () -> verify manifest checkpoint |> ignore)
            "A checkpoint with another witness tip is refused."

        checkpoint["hash"] <- box (digest 'b')
        manifest["checkpointSha256"] <- box (digest '9')

        Expect.throws
            (fun () -> verify manifest checkpoint |> ignore)
            "The signed cycle cannot name another checkpoint object."

        manifest["checkpointSha256"] <- box files.Checkpoint.Sha256
        manifest["checkpointSigningKeyId"] <- box (copyKey.ToString("D"))
        checkpoint["checkpointSigningKeyId"] <- box (copyKey.ToString("D"))

        Expect.throws
            (fun () -> verify manifest checkpoint |> ignore)
            "One signer cannot attest both producer and checkpoint custody.")

let tests = testList "backup capture signed claims" [ signedCaptureClaims ]
