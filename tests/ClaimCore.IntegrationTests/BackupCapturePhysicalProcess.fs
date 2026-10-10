module internal ClaimCore.IntegrationTests.BackupCapturePhysicalProcess

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open Expecto
open Npgsql
open ClaimCore.IntegrationTests.FixtureEnvironment
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests
open ClaimCore.IntegrationTests.BackupCapturePhysicalPrivate
open ClaimCore.TestSupport

let private file (paths: CapturePaths) name value =
    let path = Path.Combine(paths.Scratch, name)
    privateFile paths.Scratch path value
    path

let private inWitnessDatabase (writer: string) (source: string) =
    let selected = NpgsqlConnectionStringBuilder source
    selected.Database <- (NpgsqlConnectionStringBuilder writer).Database
    selected.ConnectionString

let witnessOwnerFor writer =
    inWitnessDatabase writer (witnessOwnerConnection ())

let private inputs (paths: CapturePaths) owner app writer witness =
    let witnessOwner = file paths "witness-owner.connection" (witnessOwnerFor writer)

    let witnessAudit =
        file paths "witness-audit.connection" (inWitnessDatabase writer (witnessAuditConnection ()))

    let appPath = file paths "app.connection" app

    let capability =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () ->
            failtest "Active per-test writer capability is unavailable.")

    files paths.Scratch owner writer witness
    @ [
        "CLAIMCORE_CONNECTION_FILE", appPath
        "CLAIMCORE_WITNESS_ADMIN_CONNECTION_FILE", witnessOwner
        "CLAIMCORE_WITNESS_AUDIT_CONNECTION_FILE", witnessAudit
        "CLAIMCORE_WRITER_CAPABILITY_FILE", capability
        "PGSERVICEFILE", paths.ServiceFile
        "CLAIMCORE_TEST_EXPECTED_INSTALLATION_ID", witness.Identity.InstallationId.ToString("D")
    ]

let private startSigner (paths: CapturePaths) =
    let script = Path.Combine(RepositoryRoot.find (), "eng/backup/CheckpointSigner.py")
    let start = ProcessStartInfo("python3")
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.ArgumentList.Add("-B")
    start.ArgumentList.Add(script)
    start.ArgumentList.Add("--config")
    start.ArgumentList.Add(paths.SignerConfigFile)
    let child = new Process(StartInfo = start)

    if not (child.Start()) then
        failtest "Synthetic CHECKPOINT signer did not start."

    let mutable ready = false

    for _ in 1..40 do
        if Path.Exists paths.Socket then
            ready <- true
        elif not child.HasExited then
            Thread.Sleep 100

    if not ready then
        child.Dispose()
        failtest "Synthetic CHECKPOINT signer socket was unavailable."

    child

let private stopSigner (child: Process) =
    if not child.HasExited then
        child.Kill()
        child.WaitForExit(5000) |> ignore

    child.Dispose()

let private jsonResult code (stdout: string) (stderr: string) =
    let text = if code = 0 then stdout else stderr

    try
        use document = JsonDocument.Parse text
        code, document.RootElement.Clone()
    with _ ->
        let category = Regex.Match(text, "\"reason\"\\s*:\\s*\"([a-z0-9-]{1,70})\"")

        let safe =
            if category.Success then
                category.Groups[1].Value
            else
                "unavailable"

        failtest ("Fenced capture returned no safe JSON: " + safe)

let private runPython (paths: CapturePaths) inputs tamper =
    let script =
        Path.Combine(RepositoryRoot.find (), "eng/backup/Test-FencedCapture.py")

    let primary, witness = containerIds ()
    let start = ProcessStartInfo("python3")
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true

    for argument in
        [
            "-B"
            script
            "--config"
            paths.ConfigFile
            "--primary-container"
            primary
            "--witness-container"
            witness
        ] do
        start.ArgumentList.Add(argument)

    if tamper then
        start.ArgumentList.Add("--tamper-primary")

    for name, path in inputs do
        start.Environment[name] <- path

    let previous = start.Environment["PATH"] |> Option.ofObj |> Option.defaultValue ""

    let pg =
        if OperatingSystem.IsMacOS() then
            "/opt/homebrew/opt/libpq/bin"
        else
            "/usr/bin"

    start.Environment["PATH"] <- pg + ":" + previous

    let result =
        ClaimCore.TestSupport.BoundedProcess.run start None (16 * 1024 * 1024) 300000

    jsonResult
        result.ExitCode
        (System.Text.Encoding.UTF8.GetString(result.StandardOutput))
        (System.Text.Encoding.UTF8.GetString(result.StandardError))

let withCapture paths owner app writer witness action =
    let inputs = inputs paths owner app writer witness
    use signer = startSigner paths

    try
        action (runPython paths inputs)
    finally
        stopSigner signer
