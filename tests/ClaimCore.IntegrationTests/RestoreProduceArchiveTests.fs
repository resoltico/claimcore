module ClaimCore.IntegrationTests.RestoreProduceArchiveTests

open System
open System.IO
open System.Security.Cryptography
open Expecto
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.IntegrationTests.RestoreProduceCanonicalTests

let private privateTemp () =
    let path = Path.GetTempPath()

    if
        OperatingSystem.IsMacOS()
        && (path.StartsWith("/var/", StringComparison.Ordinal)
            || path.StartsWith("/tmp/", StringComparison.Ordinal))
    then
        "/private" + path
    else
        path

let private write path bytes =
    match PrivateFileService.writeNew 1024 path bytes with
    | Ok() -> ()
    | Error _ -> failtest "Synthetic private archive file could not be created"

let private sha (bytes: byte array) =
    SHA256.HashData(ReadOnlySpan<byte>(bytes)) |> Convert.ToHexStringLower

let private withArchive action =
    let root =
        Path.Combine(privateTemp (), "claimcore-archive-" + Guid.NewGuid().ToString("N"))

    let mode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    Directory.CreateDirectory(root, mode) |> ignore

    try
        let report, index, _ = specimen ()

        let objects =
            index.ArchiveObjects
            |> List.map (fun item ->
                let bytes = Text.Encoding.ASCII.GetBytes("synthetic-" + item.RelativePath)
                write (Path.Combine(root, item.RelativePath)) bytes

                { item with
                    Sha256 = sha bytes
                    Bytes = int64 bytes.Length
                })

        let checkpointFile = Path.Combine(root, "checkpoint.json")
        let checkpointBytes = Text.Encoding.ASCII.GetBytes("{\"synthetic\":true}\n")
        write checkpointFile checkpointBytes

        let updatedReport =
            { report with
                ArchiveCustody =
                    { report.ArchiveCustody with
                        Sha256 = DatabaseRestoreArchiveObjects.rootDigest objects
                        Bytes = DatabaseRestoreArchiveObjects.totalBytes objects
                    }
                CheckpointSha256 = sha checkpointBytes
                CheckpointCustody =
                    { report.CheckpointCustody with
                        Sha256 = sha checkpointBytes
                        Bytes = int64 checkpointBytes.Length
                    }
            }

        let updatedIndex =
            { index with
                ArchiveRoot = root
                ArchiveObjects = objects
                CheckpointFile = checkpointFile
            }

        let primary =
            objects
            |> List.find (fun item -> item.Cluster = "PRIMARY" && item.Kind = "BASE")

        let witness =
            objects
            |> List.find (fun item -> item.Cluster = "WITNESS" && item.Kind = "BASE")

        action root updatedReport updatedIndex primary.CopyId witness.CopyId
    finally
        Directory.Delete(root, true)

let private byteBinding =
    testCase
        "[CC-BACKUP-001] restore consumer rehashes changed missing and swapped private archives"
        (fun _ ->
            if OperatingSystem.IsWindows() then
                ()
            else
                withArchive (fun root report index primary witness ->
                    let check value =
                        DatabaseRestoreArtifacts.verifyArchiveFilesAtRoot
                            root
                            value
                            report
                            primary
                            witness

                    check index
                    let first = index.ArchiveObjects.Head
                    let path = Path.Combine(root, first.RelativePath)
                    let original = File.ReadAllBytes(path)
                    let changed = Array.copy original
                    changed[0] <- changed[0] ^^^ 1uy
                    File.WriteAllBytes(path, changed)
                    Expect.throws (fun () -> check index) "Changed encrypted bytes refuse"
                    File.WriteAllBytes(path, original[.. original.Length - 2])
                    Expect.throws (fun () -> check index) "Short encrypted object refuses"
                    File.WriteAllBytes(path, original)
                    File.Delete(path)
                    Expect.throws (fun () -> check index) "Missing encrypted bytes refuse"
                    write path original
                    let moved = Path.Combine(root, "moved.age")
                    File.Move(path, moved)
                    File.CreateSymbolicLink(path, moved) |> ignore
                    Expect.throws (fun () -> check index) "Linked archive alias refuses"
                    File.Delete(path)
                    File.Move(moved, path)

                    Expect.throws
                        (fun () ->
                            DatabaseRestoreArtifacts.verifyArchiveFilesAtRoot
                                root
                                index
                                report
                                (Guid.NewGuid())
                                witness)
                        "A swapped old base-copy identity refuses"))

let private privateRoot =
    testCase "[CC-BACKUP-001] restore archive refuses broad-mode or linked root" (fun _ ->
        if not (OperatingSystem.IsWindows()) then
            withArchive (fun root report index primary witness ->
                let verify (configured: string) (value: RestoreEvidenceIndex) =
                    DatabaseRestoreArtifacts.verifyArchiveFilesAtRoot
                        configured
                        value
                        report
                        primary
                        witness

                let broad =
                    UnixFileMode.UserRead
                    ||| UnixFileMode.UserWrite
                    ||| UnixFileMode.UserExecute
                    ||| UnixFileMode.GroupRead

                File.SetUnixFileMode(root, broad)

                try
                    Expect.throws
                        (fun () -> verify root index)
                        "A broadly readable archive root refuses"
                finally
                    File.SetUnixFileMode(
                        root,
                        UnixFileMode.UserRead
                        ||| UnixFileMode.UserWrite
                        ||| UnixFileMode.UserExecute
                    )

                let alias = root + "-linked"
                Directory.CreateSymbolicLink(alias, root) |> ignore

                try
                    let linked = { index with ArchiveRoot = alias }

                    Expect.throws
                        (fun () -> verify alias linked)
                        "A linked archive root refuses even if the signed path matches"
                finally
                    File.Delete(alias)))

let private separatedRoots =
    testCase "[CC-BACKUP-001] signed archive and checkpoint roots cannot overlap" (fun _ ->
        let claims, index, publication = specimen ()

        let nested =
            { index with
                CheckpointRoot = index.ArchiveRoot + "/checkpoint"
            }

        Expect.throws
            (fun () -> DatabaseRestoreProduceCanonical.produce claims nested publication |> ignore)
            "A nested checkpoint root cannot impersonate separate custody")

let tests =
    testList "restored archive custody" [ byteBinding; privateRoot; separatedRoots ]
