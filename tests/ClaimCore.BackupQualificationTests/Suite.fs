module ClaimCore.BackupQualificationTests.Suite

open System
open System.Diagnostics
open System.IO
open Expecto

let private repositoryRoot () =
    let rec search (directory: DirectoryInfo | null) =
        match directory with
        | null -> failwith "Repository root was not found."
        | current when File.Exists(Path.Combine(current.FullName, "db", "postgresql-baseline.json")) ->
            current.FullName
        | current -> search current.Parent

    search (DirectoryInfo AppContext.BaseDirectory)

let private runScript tool scriptName =
    if OperatingSystem.IsWindows() then
        failwith "Backup qualification requires Bash and Docker on Linux or macOS."

    let script = Path.Combine(repositoryRoot (), "eng", "backup", scriptName)

    let start = ProcessStartInfo("/usr/bin/env")
    start.ArgumentList.Add tool
    start.ArgumentList.Add script
    start.WorkingDirectory <- repositoryRoot ()
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.Environment["PYTHONDONTWRITEBYTECODE"] <- "1"

    use runner =
        match Process.Start start with
        | null -> failwith "Backup qualification process did not start."
        | started -> started

    let output = runner.StandardOutput.ReadToEndAsync()
    let errors = runner.StandardError.ReadToEndAsync()

    if not (runner.WaitForExit(300_000)) then
        runner.Kill(true)
        failwith "Backup qualification timed out."

    output.GetAwaiter().GetResult() |> ignore
    errors.GetAwaiter().GetResult() |> ignore
    runner.ExitCode

[<Tests>]
let tests =
    testList
        "ClaimCore managed backup qualification"
        [
            testCase
                "[CC-BACKUP-001] encrypted dual-cluster backup is verified by isolated restores and rejects altered evidence"
                (fun _ ->
                    Expect.equal
                        (runScript "bash" "Test-ManagedBackup.sh")
                        0
                        "Synthetic backup and restore qualification must complete")
            testCase
                "[CC-BACKUP-001] deploy-time custody gate refuses same-host and self-certified evidence"
                (fun _ ->
                    Expect.equal
                        (runScript "python3" "Test-DeploymentAdmission.py")
                        0
                        "Independent-host deployment negatives must complete")
        ]
