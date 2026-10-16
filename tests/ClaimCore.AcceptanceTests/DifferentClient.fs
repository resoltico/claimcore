module internal ClaimCore.AcceptanceTests.DifferentClient

open ClaimCore.ClientProtocol

open System.Collections.Generic
open System.IO
open Expecto

let private copySource (fixture: PublishedFixture.Fixture) =
    let directory = Path.Combine(fixture.Directory, "client-source")
    Directory.CreateDirectory directory |> ignore

    for name in
        [
            "global.json"
            "Directory.Build.props"
            "Directory.Packages.props"
            "NuGet.Config"
        ] do
        File.Copy(Path.Combine(fixture.Root, name), Path.Combine(directory, name))

    for project in [ "ClaimCore.Cli"; "ClaimCore.ClientProtocol"; "ClaimCore.HostSecurity" ] do
        let source = Path.Combine(fixture.Root, "src", project)
        let target = Path.Combine(directory, "src", project)
        Directory.CreateDirectory target |> ignore

        for path in Directory.GetFiles(source) do
            File.Copy(path, Path.Combine(target, Path.GetFileName(path) |> nonNull))

    let contracts = Path.Combine(directory, "contracts")
    Directory.CreateDirectory contracts |> ignore

    File.Copy(
        Path.Combine(fixture.Root, "contracts/ClientContract.fs"),
        Path.Combine(contracts, "ClientContract.fs")
    )

    directory

let private differentIdentity directory =
    let catalog = Path.Combine(directory, "contracts/ClientContract.fs")
    let text = File.ReadAllText catalog
    let marker = "let fingerprint = WireContractFingerprint.create \""
    let start = text.IndexOf(marker, System.StringComparison.Ordinal)

    Expect.isGreaterThanOrEqual
        start
        0
        "Mismatch fixture reaches the client-safe compiled fingerprint"

    let valueStart = start + marker.Length

    File.WriteAllText(
        catalog,
        text.Remove(valueStart, 64).Insert(valueStart, String.replicate 64 "0")
    )

let qualify (fixture: PublishedFixture.Fixture) =
    let directory = copySource fixture
    differentIdentity directory
    let output = Path.Combine(fixture.Directory, "different-client")

    let args =
        [
            "publish"
            Path.Combine(directory, "src/ClaimCore.Cli/ClaimCore.Cli.fsproj")
            "--configuration"
            "Release"
            "-p:RestoreLockedMode=true"
            "--output"
            output
        ]

    let built =
        PublishedProcesses.run
            180_000
            (PublishedProcesses.start "dotnet" args (Dictionary<string, string>()))
            None

    FixtureFiles.write fixture.Directory "different-client-build.stdout.log" built.StandardOutput
    |> ignore

    FixtureFiles.write fixture.Directory "different-client-build.stderr.log" built.StandardError
    |> ignore

    Expect.equal
        built.ExitCode
        0
        "Compile a client with a different contract identity; see owned fixture build logs"

    let result =
        ProcessRunner.dotnet
            60_000
            (Path.Combine(output, "ClaimCore.Cli.dll"))
            [ "call" ]
            fixture.Environment
            (Some(CliCalls.frame "case.list" {| limit = 1 |}))
        |> CliCalls.parse 3

    Expect.equal
        (result.GetProperty("kind").GetString())
        "serviceFailure"
        "Different client is refused"

    Expect.equal
        (result.GetProperty("failure").GetProperty("code").GetString())
        "CONTRACT_MISMATCH"
        "Handshake refusal"
