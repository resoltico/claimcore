module internal ClaimCore.AcceptanceTests.DifferentClient

open ClaimCore.ClientProtocol

open System.Collections.Generic
open System.IO
open Expecto

let private copySourceFile (root: string) directory relative =
    let target = Path.Combine(directory, relative)
    Directory.CreateDirectory(Path.GetDirectoryName target |> nonNull) |> ignore
    File.Copy(Path.Combine(root, relative), target)

let private copyProject root directory project =
    let relative = Path.Combine("src", project)
    let source = Path.Combine(root, relative)

    for path in Directory.GetFiles source do
        let name = Path.GetFileName path |> nonNull

        if
            List.contains (Path.GetExtension path) [ ".fs"; ".fsi"; ".fsproj" ]
            || name = "packages.lock.json"
        then
            copySourceFile root directory (Path.Combine(relative, name))

let private copyNotices root directory =
    let relative = "eng/licenses/third-party-notices.json"
    let manifest = ThirdPartyNoticeManifest.read (Path.Combine(root, relative))
    copySourceFile root directory relative

    for notice in manifest.Texts do
        copySourceFile root directory (Path.Combine("eng/licenses", notice.File))

let private copySource root parent =
    let directory = Path.Combine(parent, "client-source")

    for name in
        [
            "global.json"
            "Directory.Build.props"
            "Directory.Packages.props"
            "NuGet.Config"
            "LICENSE"
            "src/Directory.Build.props"
            "contracts/ClientContract.fs"
        ] do
        copySourceFile root directory name

    for project in [ "ClaimCore.Cli"; "ClaimCore.ClientProtocol"; "ClaimCore.HostSecurity" ] do
        copyProject root directory project

    copyNotices root directory
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
    let directory = copySource fixture.Root fixture.Directory
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

    ThirdPartyNotices.qualify fixture.Root output "ClaimCore.Cli" |> ignore

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
