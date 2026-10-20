module ClaimCore.Tests.StandardSinkTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Security
open Expecto
open ClaimCore.Cli
open ClaimCore.TestSupport

let private invoke directory command arguments =
    let start =
        ProcessStartInfo(
            command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    start.WorkingDirectory <- directory
    arguments |> List.iter start.ArgumentList.Add
    BoundedProcess.run start None 8192 120000

let private controls () =
    let root = RepositoryRoot.find ()
    let directory = Directory.CreateTempSubdirectory("claimcore-standard-sinks-")

    try
        File.Copy(
            Path.Combine(root, "global.json"),
            Path.Combine(directory.FullName, "global.json")
        )

        let reference name (path: string) =
            "<Reference Include=\""
            + name
            + "\"><HintPath>"
            + SecurityElement.Escape(path)
            + "</HintPath></Reference>"

        let project =
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup>"
            + reference "ClaimCore.Cli" typeof<RemoteConfiguration>.Assembly.Location
            + reference "FSharp.Core" typeof<unit>.Assembly.Location
            + "</ItemGroup></Project>"

        File.WriteAllText(Path.Combine(directory.FullName, "Probe.csproj"), project)

        File.Copy(
            Path.Combine(root, "tests/fixtures/output/Program.cs"),
            Path.Combine(directory.FullName, "Program.cs")
        )

        let built =
            invoke
                directory.FullName
                "dotnet"
                [ "build"; "Probe.csproj"; "--configuration"; "Release"; "--output"; "bin" ]

        Expect.equal built.ExitCode 0 "Managed output probe compiles"

        let result =
            invoke
                root
                "python3"
                [
                    Path.Combine(root, "tests/fixtures/output/native_output.py")
                    "controls"
                    Path.Combine(directory.FullName, "bin/Probe.dll")
                ]

        Expect.equal
            result.ExitCode
            0
            "Native pipe/PTY/file/append/shared-offset/ownership controls"

        use report = JsonDocument.Parse result.StandardOutput

        Expect.equal
            (report.RootElement.GetProperty("passed").GetInt32())
            (if OperatingSystem.IsLinux() then 34 else 33)
            "Every native control completed"

        Expect.isTrue
            (report.RootElement.GetProperty("consoleRejectingControl").GetBoolean())
            "The control detects Console swallowing EPIPE"
    finally
        directory.Delete true

[<Tests>]
let tests =
    testCase "standard sinks preserve native output semantics and expose EPIPE/ENOSPC" controls
