namespace ClaimCore.Database

open System.Threading
open System
open System.Data
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Postgres.WitnessProtocolReconciliation
open ClaimCore.Witness

/// Historical readback of a locally sealed capture after a lost pipe response. This never
/// registers, retains, or qualifies the files for restore and cannot infer a missing receipt.
module internal DatabaseBackupCaptureReconciliation =
    open DatabaseBackupCaptureReceipt

    let private read maximum path =
        match PrivateFileService.readBinary maximum path with
        | Ok bytes when bytes.Length > 0 -> bytes
        | _ -> invalidOp "Backup capture readback file is unavailable."

    let private sha name root =
        let raw = text name root

        if raw.Length <> 64 || raw <> raw.ToLowerInvariant() then
            invalidOp "Backup capture readback digest is invalid."

        let bytes = Convert.FromHexString raw

        if Convert.ToHexStringLower bytes <> raw then
            invalidOp "Backup capture readback digest is invalid."

        bytes

    let private whenCaptured root =
        let raw = text "capturedAt" root

        let value =
            DateTimeOffset.ParseExact(
                raw,
                "yyyy-MM-ddTHH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )

        if value.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture) <> raw then
            invalidOp "Backup capture readback time is invalid."

        value

    let private held cycleRoot leaseId (manifest: JsonElement) =
        let copies = manifest.GetProperty("baseCopies")
        let primary = copies.GetProperty("primary")
        let secondary = copies.GetProperty("witness")
        let captured = whenCaptured manifest
        let nonce = text "captureNonce" manifest

        if
            nonce.Length <> 64
            || nonce
               |> Seq.exists (fun c -> not (('0' <= c && c <= '9') || ('a' <= c && c <= 'f')))
        then
            invalidOp "Backup capture readback nonce is invalid."

        let cutoff =
            {
                InstallationId = id "installationId" manifest
                LineageId = id "lineageId" manifest
                Epoch = DatabaseRestoreCanonical.number "epoch" manifest
                WriterGeneration = DatabaseRestoreCanonical.number "writerGeneration" manifest
                WitnessSequence = DatabaseRestoreCanonical.number "backupCaptureSequence" manifest
                WitnessHash = sha "backupCaptureHash" manifest
                AuthorityEvents = 0L
                AcceptedOperations = 0L
            }

        {
            Nonce = nonce
            LeaseId = leaseId
            Cutoff = cutoff
            PrimarySystemId = text "postgresSystemId" primary
            PrimaryTimeline = DatabaseRestoreCanonical.number "timeline" primary
            WitnessSystemId = text "postgresSystemId" secondary
            WitnessTimeline = DatabaseRestoreCanonical.number "timeline" secondary
            MaintenanceEvidenceSha256 = text "maintenanceEvidenceSha256" manifest
            CheckedAt = captured
            ValidUntil = captured.AddMinutes(30.)
            CycleRoot = cycleRoot
        }

    let private files cycleRoot checkpointRoot (cycleId: Guid) =
        let checkpoint = Path.Combine(checkpointRoot, cycleId.ToString("D") + ".json")

        {
            PrimaryCiphertextPath = Path.Combine(cycleRoot, "primary.tar.age")
            WitnessCiphertextPath = Path.Combine(cycleRoot, "witness.tar.age")
            CheckpointPath = checkpoint
            CycleManifestPath = Path.Combine(cycleRoot, "manifest.json")
            CycleManifestSignaturePath = Path.Combine(cycleRoot, "manifest.sig")
        }

    let private historicalKeys
        (owner: NpgsqlConnection)
        (witness: WitnessProtocol)
        (held: BackupCaptureHeld)
        (claims: BackupCaptureClaims)
        =
        task {
            use transaction = owner.BeginTransaction(IsolationLevel.ReadCommitted)

            let! copyKey, copyHolder =
                DatabaseRestoreSignedEvidence.historicalSigner
                    owner
                    transaction
                    witness
                    claims.CopySigningKeyId
                    CopySignerPurpose.CopyAttestor
                    held.Cutoff.Epoch
                    held.Cutoff.WitnessSequence

            let! checkpointKey, checkpointHolder =
                DatabaseRestoreSignedEvidence.historicalSigner
                    owner
                    transaction
                    witness
                    claims.CheckpointSigningKeyId
                    CopySignerPurpose.Checkpoint
                    held.Cutoff.Epoch
                    held.Cutoff.WitnessSequence

            transaction.Commit()

            if
                copyHolder = checkpointHolder
                || CryptographicOperations.FixedTimeEquals(copyKey, checkpointKey)
            then
                invalidOp "Backup capture historical signer custody overlaps."

            return copyKey, checkpointKey
        }

    let private requireSignatures
        (manifest: byte array)
        (checkpoint: byte array)
        (digests: BackupCaptureFileDigests)
        copyKey
        checkpointKey
        (manifestSignature: byte array)
        (checkpointSignature: byte array)
        =
        if
            manifestSignature.Length <> 64
            || checkpointSignature.Length <> 64
            || (SHA256.HashData(manifest) |> Convert.ToHexStringLower)
               <> digests.Manifest.Sha256
            || (SHA256.HashData(checkpoint) |> Convert.ToHexStringLower)
               <> digests.Checkpoint.Sha256
            || (SHA256.HashData(manifestSignature) |> Convert.ToHexStringLower)
               <> digests.ManifestSignature.Sha256
            || (SHA256.HashData(checkpointSignature) |> Convert.ToHexStringLower)
               <> digests.CheckpointSignature.Sha256
            || not (ManagedCopySignature.verify copyKey manifest manifestSignature)
            || not (ManagedCopySignature.verify checkpointKey checkpoint checkpointSignature)
        then
            invalidOp "Backup capture historical signatures differ."


    let private verifySigned
        (owner: NpgsqlConnection)
        (witness: WitnessProtocol)
        held
        files
        digests
        manifest
        checkpoint
        =
        task {
            use manifestDocument = parse manifest
            use checkpointDocument = parse checkpoint

            let claims =
                DatabaseBackupCaptureClaims.verify
                    held
                    digests
                    manifestDocument.RootElement
                    checkpointDocument.RootElement

            do!
                witness.VerifyHistoricalTip(
                    held.Cutoff.WitnessSequence,
                    held.Cutoff.WitnessHash,
                    CancellationToken.None
                )

            let! copyKey, checkpointKey = historicalKeys owner witness held claims
            let manifestSignature = read 64 files.CycleManifestSignaturePath

            let checkpointSignaturePath =
                Path.ChangeExtension(files.CheckpointPath, ".sig")
                |> Option.ofObj
                |> Option.defaultWith (fun () ->
                    invalidOp "Backup checkpoint signature path is invalid.")

            let checkpointSignature = read 64 checkpointSignaturePath

            try
                requireSignatures
                    manifest
                    checkpoint
                    digests
                    copyKey
                    checkpointKey
                    manifestSignature
                    checkpointSignature

                return claims
            finally
                CryptographicOperations.ZeroMemory(copyKey)
                CryptographicOperations.ZeroMemory(checkpointKey)
                CryptographicOperations.ZeroMemory(manifestSignature)
                CryptographicOperations.ZeroMemory(checkpointSignature)
        }

    let inspect
        (owner: NpgsqlConnection)
        (witness: WitnessProtocol)
        archiveRoot
        checkpointRoot
        (leaseId: Guid)
        =
        task {
            if leaseId = Guid.Empty then
                invalidOp "Backup capture readback lease identity is invalid."

            let cycleRoot = Path.Combine(archiveRoot, leaseId.ToString("D"))
            let receiptPath = Path.Combine(cycleRoot, "capture-receipt.json")
            let receiptBytes = read 16384 receiptPath
            let cycleId = DatabaseBackupCaptureReceipt.cycleIdentity receiptBytes leaseId
            let paths = files cycleRoot checkpointRoot cycleId
            let digests = DatabaseBackupCapturePaths.inspect cycleRoot checkpointRoot paths
            let manifest = read 131072 paths.CycleManifestPath
            let checkpoint = read 16384 paths.CheckpointPath

            try
                use manifestDocument = parse manifest
                let expectedHeld = held cycleRoot leaseId manifestDocument.RootElement

                if
                    expectedHeld.Cutoff.InstallationId <> witness.Identity.InstallationId
                    || expectedHeld.Cutoff.LineageId <> witness.Identity.LineageId
                    || expectedHeld.Cutoff.Epoch <> witness.Identity.Epoch
                then
                    invalidOp "Backup capture readback belongs to another installation."

                let! claims =
                    verifySigned owner witness expectedHeld paths digests manifest checkpoint

                if claims.CycleId <> cycleId then
                    invalidOp "Backup capture receipt cycle differs."

                let receipt =
                    DatabaseBackupCaptureReceipt.expected expectedHeld cycleId leaseId digests

                let expected = DatabaseBackupCaptureResponses.receipt "SEALED" receipt

                if not (CryptographicOperations.FixedTimeEquals(expected, receiptBytes)) then
                    invalidOp "Backup capture persisted receipt or exact files changed."

                return receipt
            finally
                CryptographicOperations.ZeroMemory(manifest)
                CryptographicOperations.ZeroMemory(checkpoint)
        }
