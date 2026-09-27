module ClaimCore.BackupQualificationTests.Suite

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Expecto

let private expectedRefusalStages =
    [
        "Owner-selected callback hash passed", "owner-verifier-hash"
        "A shared copy/checkpoint signing key passed", "shared-signing-key"
        "Linked private configuration passed", "linked-configuration"
        "Linked private ancestor passed", "linked-ancestor"
        "The unfenced owner capture CLI remained available", "unfenced-capture"
        "A checkpoint signed by the copy attestor passed", "shared-checkpoint-attestor"
        "Caller-selected full restore claim passed", "caller-restore-claim"
        "Oversized decrypted backup passed", "backup-size-bound"
        "Missing managed witness WAL copy passed", "missing-witness-wal"
        "Unowned WAL timeline passed", "unowned-wal-timeline"
        "Malformed PostgreSQL WAL passed", "malformed-wal"
        "Changed WAL segment under an existing name passed", "changed-wal-segment"
        "Wrong-installation signed backup passed", "wrong-installation"
        "Stale but correctly signed backup passed", "stale-backup"
        "Missing independent checkpoint passed", "missing-checkpoint"
        "Altered signed manifest passed", "altered-manifest"
        "Altered witness ciphertext passed", "altered-witness-ciphertext"
    ]

let private safeStage (diagnostics: string) =
    let toolReason =
        Regex.Match(diagnostics, "\"reason\"\\s*:\\s*\"([a-z0-9-]{1,70})\"")

    let shellStage =
        Regex.Match(
            diagnostics,
            "(?:backup-test-stage=line-[0-9]{1,4}|checkpoint-signer-stage=[a-z-]{1,70})"
        )

    if toolReason.Success then
        "backup-reason-" + toolReason.Groups[1].Value
    elif shellStage.Success then
        shellStage.Value
    else
        expectedRefusalStages
        |> List.tryPick (fun (message, category) ->
            if diagnostics.Contains(message, StringComparison.Ordinal) then
                Some category
            else
                None)
        |> Option.defaultValue "stage-unavailable"

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
    let diagnostics = errors.GetAwaiter().GetResult()

    if runner.ExitCode <> 0 then
        failtestf "Synthetic qualification stopped at %s." (safeStage diagnostics)

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
                        (safeStage "private fixture detail; backup-test-stage=line-143")
                        "backup-test-stage=line-143"
                        "Only a bounded shell stage leaves failed qualification diagnostics."

                    Expect.equal
                        (safeStage "private fixture detail without a safe stage")
                        "stage-unavailable"
                        "Private script diagnostics are not emitted by the test."

                    Expect.equal
                        (safeStage
                            "{\"status\":\"quarantined\",\"reason\":\"pg-verifybackup-failed\"}")
                        "backup-reason-pg-verifybackup-failed"
                        "A closed backup refusal category survives redaction."

                    Expect.equal
                        (runScript "python3" "Test-ToolVersions.py")
                        0
                        "Backup tool version boundaries must remain exact"

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
