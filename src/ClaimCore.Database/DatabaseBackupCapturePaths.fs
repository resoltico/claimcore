namespace ClaimCore.Database

open System
open System.IO
open ClaimCore.HostSecurity

[<NoEquality; NoComparison>]
type internal BackupCaptureFileDigest = { Bytes: int64; Sha256: string }

[<NoEquality; NoComparison>]
type internal BackupCaptureFileDigests =
    {
        Primary: BackupCaptureFileDigest
        Witness: BackupCaptureFileDigest
        Checkpoint: BackupCaptureFileDigest
        CheckpointSignature: BackupCaptureFileDigest
        Manifest: BackupCaptureFileDigest
        ManifestSignature: BackupCaptureFileDigest
    }

/// The owner reopens every capture artifact through nofollow private handles. A path supplied by
/// the producer cannot redirect the verifier outside the lease's exact private roots.
module internal DatabaseBackupCapturePaths =
    let private beneath (root: string) (path: string) =
        if not (Path.IsPathFullyQualified root && Path.IsPathFullyQualified path) then
            invalidOp "Backup capture path is not absolute."

        let canonicalRoot = Path.GetFullPath root
        let canonicalPath = Path.GetFullPath path
        let relative = Path.GetRelativePath(canonicalRoot, canonicalPath)

        if
            relative = "."
            || relative = ".."
            || relative.StartsWith(
                ".." + string Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            )
            || Path.IsPathFullyQualified relative
        then
            invalidOp "Backup capture path leaves its private root."

        let parent =
            Path.GetDirectoryName canonicalPath
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Backup capture file has no parent.")

        match PrivateFileService.requirePrivateDirectory parent with
        | Ok() -> ()
        | Error _ -> invalidOp "Backup capture parent is not owner-private."

    let private digest root maximum path =
        beneath root path

        match PrivateFileService.hashPrivateFile maximum path with
        | Ok(bytes, sha) when bytes > 0L ->
            {
                Bytes = bytes
                Sha256 = Convert.ToHexStringLower sha
            }
        | _ -> invalidOp "Backup capture file is missing, changed, or unbounded."

    let inspect cycleRoot checkpointRoot (files: BackupCaptureFiles) =
        let cycle = Path.GetFullPath cycleRoot
        let checkpoint = Path.GetFullPath checkpointRoot

        let checkpointSignature =
            Path.ChangeExtension(files.CheckpointPath, ".sig")
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
                invalidOp "Backup checkpoint signature path is invalid.")

        match PrivateFileService.requirePrivateDirectory cycle with
        | Ok() -> ()
        | Error _ -> invalidOp "Backup capture root is not owner-private."

        match PrivateFileService.requirePrivateDirectory checkpoint with
        | Ok() -> ()
        | Error _ -> invalidOp "Backup checkpoint root is not owner-private."

        if
            cycle = checkpoint
            || checkpoint.StartsWith(
                cycle + string Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            )
            || cycle.StartsWith(
                checkpoint + string Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            )
        then
            invalidOp "Backup checkpoint custody is not separate."

        {
            Primary = digest cycleRoot (1L <<< 40) files.PrimaryCiphertextPath
            Witness = digest cycleRoot (1L <<< 40) files.WitnessCiphertextPath
            Checkpoint = digest checkpointRoot 16384L files.CheckpointPath
            CheckpointSignature = digest checkpointRoot 64L checkpointSignature
            Manifest = digest cycleRoot 131072L files.CycleManifestPath
            ManifestSignature = digest cycleRoot 64L files.CycleManifestSignaturePath
        }
