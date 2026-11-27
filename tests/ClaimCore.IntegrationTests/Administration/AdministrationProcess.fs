module internal ClaimCore.IntegrationTests.AdministrationProcess

open ClaimCore.CommandLine
open ClaimCore.Application.Storage
open ClaimCore.TestSupport
open System.Threading
open Npgsql

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Threading.Tasks
open Expecto

let start arguments =
    let config =
        ProcessStartInfo(
            "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    for argument in arguments do
        config.ArgumentList.Add argument

    for key in
        config.Environment.Keys
        |> Seq.filter _.StartsWith("CLAIMCORE_", StringComparison.Ordinal)
        |> Seq.toList do
        config.Environment.Remove key |> ignore

    config

let execute (config: ProcessStartInfo) =
    task {
        use child =
            Process.Start(config)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failtest "Process not started.")

        try
            let output = child.StandardOutput.ReadToEndAsync()
            let errors = child.StandardError.ReadToEndAsync()
            do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 60.)
            let! output = output
            let! errors = errors
            return child.ExitCode, output, errors
        finally
            if not child.HasExited then
                child.Kill(true)
    }

let publishConfiguration configuration directory =
    task {
        let project =
            Path.Combine(
                ClaimCore.TestSupport.RepositoryRoot.find (),
                "src/ClaimCore.Server/ClaimCore.Server.fsproj"
            )

        let config =
            start
                [
                    "publish"
                    project
                    "--configuration"
                    configuration
                    "--no-build"
                    "--no-restore"
                    "--output"
                    directory
                ]

        let evidenceDirectory = Path.GetDirectoryName(Path.GetFullPath directory) |> nonNull

        let retain (stdout: byte array) (stderr: byte array) =
            for name, bytes in [ "publish.stdout", stdout; "publish.stderr", stderr ] do
                let path = Path.Combine(evidenceDirectory, name)
                File.WriteAllBytes(path, bytes)
                File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

            Console.Error.WriteLine(
                "Owner publish diagnostics retained privately: " + evidenceDirectory
            )

        let result =
            try
                ClaimCore.TestSupport.BoundedProcess.run config None 1_048_576 60_000
            with :? ClaimCore.TestSupport.BoundedProcessFailure as failure ->
                ClaimCore.TestSupport.FixtureDisposal.afterFailure
                    failure
                    [ fun () -> retain failure.StandardOutput failure.StandardError ]

                reraise ()

        try
            Expect.equal
                result.ExitCode
                0
                ("Publish compiled owner binary; build the current configuration inputs first. Private failure diagnostics: "
                 + evidenceDirectory)
        with failure ->
            ClaimCore.TestSupport.FixtureDisposal.afterFailure
                failure
                [ fun () -> retain result.StandardOutput result.StandardError ]

        return Path.Combine(directory, "claimcore-server.dll")
    }

let publish directory =
    publishConfiguration "Release" directory

/// Admit only the production diagnostic allowlist; no provider text or arbitrary fields survive.
let requireDiagnostics (errors: string) =
    for line in errors.Split('\n', StringSplitOptions.RemoveEmptyEntries) do
        use diagnostic = JsonDocument.Parse line
        let value = diagnostic.RootElement

        Expect.equal
            (value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
            (set [ "kind"; "attempt"; "correlation"; "phase"; "classification" ])
            "Closed safe diagnostic fields"

        Expect.equal (value.GetProperty("kind").GetString()) "storageFailure" "Diagnostic channel"

        let guid (name: string) =
            let text = value.GetProperty(name).GetString() |> nonNull
            let parsed = Guid.ParseExact(text, "D")
            Expect.notEqual parsed Guid.Empty "Internally generated correlation"

        guid "attempt"
        guid "correlation"

        Expect.contains
            [
                "Open"
                "Read"
                "Head"
                "Append"
                "Projection"
                "Commit"
                "Session"
                "Housekeeping"
                "Cleanup"
            ]
            (value.GetProperty("phase").GetString() |> nonNull)
            "Bounded phase"

        Expect.contains
            [
                "Cancelled"
                "Timeout"
                "Provider"
                "InputOutput"
                "Installation"
                "Internal"
            ]
            (value.GetProperty("classification").GetString() |> nonNull)
            "Bounded class"

let configure (config: ProcessStartInfo) connectionFile dataKeyFile =
    config.Environment["CLAIMCORE_ADMIN_CONNECTION_FILE"] <- connectionFile
    config.Environment["CLAIMCORE_DATA_KEY_FILE"] <- dataKeyFile

let invokeWith configureExtra binary connectionFile dataKeyFile arguments expected =
    task {
        let config = start (binary :: arguments)
        configure config connectionFile dataKeyFile
        configureExtra config
        let! code, output, errors = execute config

        Expect.equal
            code
            expected
            ("Administration process exit: "
             + String.concat " " arguments
             + ". Public reply: "
             + output)

        if expected = 0 then
            Expect.equal errors.Length 0 "Successful commands remain quiet"
        else
            requireDiagnostics errors

        Expect.equal
            (output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length)
            1
            "Exactly one JSON object"

        use document = JsonDocument.Parse output
        Expect.equal document.RootElement.ValueKind JsonValueKind.Object "Object response"
        return document.RootElement.Clone()
    }

let invoke binary connectionFile dataKeyFile arguments expected =
    invokeWith ignore binary connectionFile dataKeyFile arguments expected

let privateText directory name (value: string) =
    let path = Path.Combine(directory, name)
    File.WriteAllText(path, value)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    path


let beginRestore (source: NpgsqlDataSource) (witness: NpgsqlDataSource) =
    task {
        let token = CancellationToken.None
        let transition = Guid.NewGuid()

        let! installation, primary =
            DatabaseSession.read source token (fun connection ->
                task {
                    let! installation = Baseline.require connection token
                    let! primary = WitnessPrimary.read connection token
                    return installation.InstallationId, primary
                })

        let! rows =
            DatabaseSession.read witness token (fun connection ->
                DatabaseSession.rows
                    connection
                    "SELECT EXISTS(SELECT FROM claimcore_witness.installations WHERE installation=$1)"
                    [| box installation |]
                    token)

        let enrolled = DatabaseSession.value<bool> 0 rows.Head

        if not enrolled then
            let! _ = WitnessEnrollment.enroll source witness installation token
            ()

        use! connection = witness.OpenConnectionAsync token

        let! fenced =
            DatabaseSession.rows
                connection
                "SELECT claimcore_witness.fence($1,$2,$3)"
                [| box installation; box primary.Generation; box transition |]
                token

        Expect.isTrue
            (DatabaseSession.value<bool> 0 fenced.Head)
            "The owner fenced the current generation"

        if enrolled then
            let! gap =
                DatabaseSession.rows
                    connection
                    "SELECT claimcore_witness.record_gap($1,$2,'DISCONTINUOUS_HISTORY')"
                    [| box installation; box transition |]
                    token

            Expect.isTrue
                (DatabaseSession.value<bool> 0 gap.Head)
                "The isolated fixture has no archive proof for its second activation"

        return transition
    }

let configureRestore witnessFile (transition: Guid) (config: ProcessStartInfo) =
    config.Environment["CLAIMCORE_WITNESS_ADMIN_CONNECTION_FILE"] <- witnessFile
    config.Environment["CLAIMCORE_RESTORE_TRANSITION"] <- transition.ToString("D")
