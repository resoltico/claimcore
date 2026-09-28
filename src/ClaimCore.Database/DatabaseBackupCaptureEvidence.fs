namespace ClaimCore.Database

open System
open System.Data
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres

/// Owner-side physical checks for a captured BASE pair. The receipt is local, private and
/// CAPTURED_UNVERIFIED; witnessed REGISTER/VERIFY, independent custody and a test restore remain
/// separate admission requirements.
[<Sealed>]
type internal DatabaseBackupCaptureEvidence
    (
        owner: NpgsqlConnection,
        witnessOwner: NpgsqlConnection,
        archiveRoot: string,
        checkpointRoot: string
    ) =
    let mutable sealedFiles: (BackupCaptureFiles * BackupCaptureFileDigests) option =
        None

    let privateDirectory path =
        match PrivateFileService.requirePrivateDirectory path with
        | Ok() -> ()
        | Error _ -> invalidOp "Backup capture custody root is not owner-private."

    let separateRoots () =
        let archive = Path.GetFullPath archiveRoot
        let checkpoint = Path.GetFullPath checkpointRoot
        let separator = string Path.DirectorySeparatorChar

        if
            archive = checkpoint
            || archive.StartsWith(checkpoint + separator, StringComparison.Ordinal)
            || checkpoint.StartsWith(archive + separator, StringComparison.Ordinal)
        then
            invalidOp "Backup capture custodies overlap."

    let read maximum path =
        match PrivateFileService.readBinary maximum path with
        | Ok bytes when bytes.Length > 0 -> bytes
        | _ -> invalidOp "Backup capture signed file is unavailable."

    let parse bytes =
        DatabaseRestoreCanonical.parse bytes
        |> Option.defaultWith (fun () -> invalidOp "Backup capture signed file is noncanonical.")

    let control (connection: NpgsqlConnection) =
        use command =
            new NpgsqlCommand(
                "SELECT s.system_identifier::text,c.timeline_id::bigint "
                + "FROM pg_control_system() s CROSS JOIN pg_control_checkpoint() c",
                connection
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Backup capture cluster control is unavailable."

        let systemId, timeline = reader.GetString(0), reader.GetInt64(1)

        if reader.Read() || timeline < 1L then
            invalidOp "Backup capture cluster control is ambiguous."

        systemId, timeline

    let maintenance (cutoff: BackupCaptureCutoff) primary witness =
        let source =
            String.Join(
                "|",
                [|
                    cutoff.InstallationId.ToString("D")
                    cutoff.LineageId.ToString("D")
                    string cutoff.Epoch
                    string cutoff.WriterGeneration
                    string cutoff.WitnessSequence
                    Convert.ToHexStringLower cutoff.WitnessHash
                    string cutoff.AuthorityEvents
                    string cutoff.AcceptedOperations
                    fst primary
                    string (snd primary)
                    fst witness
                    string (snd witness)
                |]
            )

        SHA256.HashData(Encoding.ASCII.GetBytes(source)) |> Convert.ToHexStringLower

    let registeredSigners claims =
        use transaction = owner.BeginTransaction(IsolationLevel.ReadCommitted)

        let copyKey, copyHolder =
            DatabaseRestoreSignedEvidence.signer
                owner
                transaction
                claims.CopySigningKeyId
                CopySignerPurpose.CopyAttestor

        let checkpointKey, checkpointHolder =
            DatabaseRestoreSignedEvidence.signer
                owner
                transaction
                claims.CheckpointSigningKeyId
                CopySignerPurpose.Checkpoint

        transaction.Commit()

        if
            copyHolder = checkpointHolder
            || CryptographicOperations.FixedTimeEquals(copyKey, checkpointKey)
        then
            invalidOp "Backup capture signers lack independent custody."

        copyKey, checkpointKey

    let verifySigned held files (digests: BackupCaptureFileDigests) =
        let manifest = read 131072 files.CycleManifestPath
        let checkpoint = read 16384 files.CheckpointPath
        let manifestSignature = read 64 files.CycleManifestSignaturePath

        let checkpointSignaturePath =
            Path.ChangeExtension(files.CheckpointPath, ".sig")
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
                invalidOp "Backup checkpoint signature path is invalid.")

        let checkpointSignature = read 64 checkpointSignaturePath

        try
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
            then
                invalidOp "Backup capture signed bytes changed after opening."

            use manifestDocument = parse manifest
            use checkpointDocument = parse checkpoint

            let claims =
                DatabaseBackupCaptureClaims.verify
                    held
                    digests
                    manifestDocument.RootElement
                    checkpointDocument.RootElement

            let copyKey, checkpointKey = registeredSigners claims

            try
                if
                    not (ManagedCopySignature.verify copyKey manifest manifestSignature)
                    || not (
                        ManagedCopySignature.verify checkpointKey checkpoint checkpointSignature
                    )
                then
                    invalidOp "Backup capture signature differs from registered custody."
            finally
                CryptographicOperations.ZeroMemory(copyKey)
                CryptographicOperations.ZeroMemory(checkpointKey)

            claims
        finally
            CryptographicOperations.ZeroMemory(manifest)
            CryptographicOperations.ZeroMemory(checkpoint)
            CryptographicOperations.ZeroMemory(manifestSignature)
            CryptographicOperations.ZeroMemory(checkpointSignature)

    interface IBackupCaptureEvidence with
        member _.Describe(cutoff, nonce, leaseId, checkedAt, expiresAt) =
            privateDirectory archiveRoot
            privateDirectory checkpointRoot
            separateRoots ()
            let primary = control owner
            let witness = control witnessOwner

            if fst primary = fst witness then
                invalidOp "Backup capture clusters share PostgreSQL identity."

            let cycleRoot = Path.Combine(archiveRoot, leaseId.ToString("D"))

            match PrivateFileService.ensureDirectory cycleRoot with
            | Ok _ -> ()
            | Error _ -> invalidOp "Backup capture cycle root cannot be reserved."

            Some
                {
                    Nonce = nonce
                    LeaseId = leaseId
                    Cutoff = cutoff
                    PrimarySystemId = fst primary
                    PrimaryTimeline = snd primary
                    WitnessSystemId = fst witness
                    WitnessTimeline = snd witness
                    MaintenanceEvidenceSha256 = maintenance cutoff primary witness
                    CheckedAt = checkedAt
                    ValidUntil = expiresAt
                    CycleRoot = cycleRoot
                }

        member _.Seal(held, files, cancellationToken: CancellationToken) =
            task {
                cancellationToken.ThrowIfCancellationRequested()
                let digests = DatabaseBackupCapturePaths.inspect held.CycleRoot checkpointRoot files
                cancellationToken.ThrowIfCancellationRequested()

                if
                    digests.ManifestSignature.Bytes <> 64L
                    || digests.CheckpointSignature.Bytes <> 64L
                then
                    invalidOp "Backup capture detached signature is invalid."

                let claims = verifySigned held files digests
                cancellationToken.ThrowIfCancellationRequested()

                let receiptHash =
                    DatabaseBackupCaptureReceipt.digest
                        claims.CycleId
                        held.LeaseId
                        held.Nonce
                        digests

                let receipt =
                    {
                        Nonce = held.Nonce
                        LeaseId = held.LeaseId
                        CycleReceiptId = claims.CycleId
                        WitnessSequence = held.Cutoff.WitnessSequence
                        WitnessHash = Convert.ToHexStringLower held.Cutoff.WitnessHash
                        ReceiptSha256 = receiptHash
                    }

                let path = Path.Combine(held.CycleRoot, "capture-receipt.json")

                match
                    PrivateFileService.writeNew
                        16384
                        path
                        (DatabaseBackupCaptureResponses.receipt "SEALED" receipt)
                with
                | Ok() -> ()
                | Error _ -> invalidOp "Backup capture receipt cannot be durably recorded."

                sealedFiles <- Some(files, digests)
                return Some receipt
            }

        member _.Observe(receipt, cancellationToken: CancellationToken) =
            task {
                cancellationToken.ThrowIfCancellationRequested()

                match sealedFiles with
                | None -> return false
                | Some(files, earlier) ->
                    let cycleRoot =
                        Path.GetDirectoryName files.CycleManifestPath
                        |> Option.ofObj
                        |> Option.defaultWith (fun () ->
                            invalidOp "Backup capture cycle path is invalid.")

                    let path = Path.Combine(cycleRoot, "capture-receipt.json")
                    let expected = DatabaseBackupCaptureResponses.receipt "SEALED" receipt

                    match PrivateFileService.readBinary 16384 path with
                    | Ok actual when CryptographicOperations.FixedTimeEquals(actual, expected) ->
                        let current =
                            DatabaseBackupCapturePaths.inspect cycleRoot checkpointRoot files

                        cancellationToken.ThrowIfCancellationRequested()

                        let same (left: BackupCaptureFileDigest) (right: BackupCaptureFileDigest) =
                            left.Bytes = right.Bytes && left.Sha256 = right.Sha256

                        return
                            same current.Primary earlier.Primary
                            && same current.Witness earlier.Witness
                            && same current.Checkpoint earlier.Checkpoint
                            && same current.CheckpointSignature earlier.CheckpointSignature
                            && same current.Manifest earlier.Manifest
                            && same current.ManifestSignature earlier.ManifestSignature
                    | _ -> return false
            }
