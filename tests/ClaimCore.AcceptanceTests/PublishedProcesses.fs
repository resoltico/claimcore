module internal ClaimCore.AcceptanceTests.PublishedProcesses

open System
open System.Diagnostics
open System.Collections.Generic
open System.IO
open System.Threading
open Expecto

let start executable arguments (environment: IReadOnlyDictionary<string, string>) =
    let value =
        ProcessStartInfo(
            executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    ClaimCore.TestSupport.CliProcessEnvironment.clearInherited value

    for argument in arguments do
        value.ArgumentList.Add argument

    for pair in environment do
        value.Environment[pair.Key] <- pair.Value

    value.Environment["DOTNET_NOLOGO"] <- "1"
    value.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
    value

let run timeout value input =
    ClaimCore.TestSupport.BoundedProcess.run value input 1_048_576 timeout

let publish root directory name =
    let output = Path.Combine(directory, name)

    let args =
        [
            "publish"
            Path.Combine(root, "src", name, name + ".fsproj")
            "--configuration"
            "Release"
            "--no-build"
            "--no-restore"
            "--output"
            output
        ]

    let result = run 120_000 (start "dotnet" args (Dictionary<string, string>())) None
    Expect.equal result.ExitCode 0 ("Publish " + name)

    if name = "ClaimCore.Cli" then
        let forbidden =
            [
                "ClaimCore.Domain"
                "ClaimCore.RecordFormat"
                "ClaimCore.Application"
                "ClaimCore.Contracts"
                "ClaimCore.Hosting"
                "ClaimCore.Postgres"
            ]

        for assembly in forbidden do
            Expect.isFalse
                (File.Exists(Path.Combine(output, assembly + ".dll")))
                ("Published client excludes " + assembly)

        let dependencies = File.ReadAllText(Path.Combine(output, name + ".deps.json"))

        for assembly in forbidden do
            Expect.isFalse
                (dependencies.Contains(assembly, StringComparison.Ordinal))
                ("Resolved client closure excludes " + assembly)

    Path.Combine(output, name + ".dll")

type Web(binary, environment, directory) =
    let value = start "dotnet" [ binary ] environment
    let child = Process.Start value |> nonNull
    let deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30.))

    let output =
        ClaimCore.TestSupport.BoundedProcess.read 8_388_608 child.StandardOutput.BaseStream deadline

    let errors =
        ClaimCore.TestSupport.BoundedProcess.read 1_048_576 child.StandardError.BaseStream deadline

    member _.HasExited = child.HasExited

    interface IDisposable with
        member _.Dispose() =
            try
                if not child.HasExited then
                    child.Kill(true)

                child.WaitForExit()

                FixtureFiles.write directory "web.stdout" (output.GetAwaiter().GetResult())
                |> ignore

                FixtureFiles.write directory "web.stderr" (errors.GetAwaiter().GetResult())
                |> ignore
            finally
                deadline.Dispose()
                child.Dispose()
