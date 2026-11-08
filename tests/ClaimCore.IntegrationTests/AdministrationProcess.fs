module internal ClaimCore.IntegrationTests.AdministrationProcess

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Threading.Tasks
open Expecto

let private start arguments =
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
                "src/ClaimCore.Database/ClaimCore.Database.fsproj"
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

        return Path.Combine(directory, "claimcore-database.dll")
    }

let publish directory =
    publishConfiguration "Release" directory

let invoke binary connectionFile ownerFile arguments expected =
    task {
        let config = start (binary :: arguments)
        config.Environment["CLAIMCORE_ADMIN_CONNECTION_FILE"] <- connectionFile
        config.Environment["CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE"] <- ownerFile
        let! code, output, errors = execute config

        Expect.equal
            code
            expected
            ("Administration process exit: "
             + String.concat " " arguments
             + ". Public reply: "
             + output)

        Expect.equal errors.Length 0 "No provider/private detail on stderr"

        Expect.equal
            (output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length)
            1
            "Exactly one JSON object"

        use document = JsonDocument.Parse output
        Expect.equal document.RootElement.ValueKind JsonValueKind.Object "Object response"
        return document.RootElement.Clone()
    }

let privateText directory name (value: string) =
    let path = Path.Combine(directory, name)
    File.WriteAllText(path, value)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    path
