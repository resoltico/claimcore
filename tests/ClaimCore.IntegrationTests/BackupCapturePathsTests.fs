module ClaimCore.IntegrationTests.BackupCapturePathsTests

open System
open System.IO
open System.Text
open Expecto
open ClaimCore.Database
open ClaimCore.HostSecurity

let private root () =
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
        Path.Combine(canonical, "claimcore-capture-files-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(path) |> ignore

    File.SetUnixFileMode(
        path,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )

    path

let private store path (value: string) =
    let bytes = Encoding.ASCII.GetBytes value

    match PrivateFileService.writeNew 4096 path bytes with
    | Ok() -> ()
    | Error _ -> failtest "Synthetic private file admission failed."

let private paths () =
    let parent = root ()
    let cycle = Path.Combine(parent, "cycle")
    let checkpoint = Path.Combine(parent, "checkpoint")

    for directory in [ cycle; checkpoint ] do
        Directory.CreateDirectory(directory) |> ignore

        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

    let files =
        {
            PrimaryCiphertextPath = Path.Combine(cycle, "primary.age")
            WitnessCiphertextPath = Path.Combine(cycle, "witness.age")
            CheckpointPath = Path.Combine(checkpoint, "checkpoint.json")
            CycleManifestPath = Path.Combine(cycle, "manifest.json")
            CycleManifestSignaturePath = Path.Combine(cycle, "manifest.sig")
        }

    store files.PrimaryCiphertextPath "synthetic-primary"
    store files.WitnessCiphertextPath "synthetic-witness"
    store files.CheckpointPath "synthetic-checkpoint"
    store (Path.Combine(checkpoint, "checkpoint.sig")) "synthetic-checkpoint-signature"
    store files.CycleManifestPath "synthetic-manifest"
    store files.CycleManifestSignaturePath "synthetic-signature"
    parent, cycle, checkpoint, files

let private privateCaptureFiles =
    testCase "[CC-BACKUP-001] owner hashes only private exact-root capture files" (fun _ ->
        let parent, cycle, checkpoint, files = paths ()

        try
            let inspected = DatabaseBackupCapturePaths.inspect cycle checkpoint files
            Expect.equal inspected.Primary.Bytes 17L "Owner streams exact ciphertext bytes."
            Expect.equal inspected.Manifest.Bytes 18L "Owner checks the signed manifest bytes."

            let elsewhere =
                { files with
                    PrimaryCiphertextPath = files.CheckpointPath
                }

            Expect.throws
                (fun () -> DatabaseBackupCapturePaths.inspect cycle checkpoint elsewhere |> ignore)
                "A capture path cannot cross into independent checkpoint custody."

            let linked = Path.Combine(cycle, "linked.age")
            File.CreateSymbolicLink(linked, files.PrimaryCiphertextPath) |> ignore

            let alias =
                { files with
                    PrimaryCiphertextPath = linked
                }

            Expect.throws
                (fun () -> DatabaseBackupCapturePaths.inspect cycle checkpoint alias |> ignore)
                "A symbolic link cannot redirect a private ciphertext read."
        finally
            Directory.Delete(parent, true))

let tests = testList "backup capture private files" [ privateCaptureFiles ]
