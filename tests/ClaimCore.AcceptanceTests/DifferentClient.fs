module internal ClaimCore.AcceptanceTests.DifferentClient

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Xml.Linq
open Expecto
open ClaimCore.Contract
open ClaimCore.Json

let private copySourceFile (root: string) directory relative =
    let target = Path.Combine(directory, relative)
    Directory.CreateDirectory(Path.GetDirectoryName target |> nonNull) |> ignore
    File.Copy(Path.Combine(root, relative), target, true)

let private includes name (project: XDocument) =
    project.Descendants(XName.Get name)
    |> Seq.map (fun item -> item.Attribute(XName.Get "Include") |> nonNull |> _.Value)

let rec private copyProject root directory (visited: HashSet<string>) relative =
    if visited.Add relative then
        copySourceFile root directory relative
        let source = Path.Combine(root, relative)
        let folder = Path.GetDirectoryName source |> nonNull
        let project = XDocument.Load source

        let repositoryRelative item =
            let path = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(folder, item)))

            if path.StartsWith("..", StringComparison.Ordinal) then
                invalidOp "Client fixture source must stay inside the repository."

            path

        for item in includes "Compile" project do
            copySourceFile root directory (repositoryRelative item)

        for item in includes "EmbeddedResource" project do
            let sourcePattern = Path.GetFullPath(Path.Combine(folder, item))
            let parent = Path.GetDirectoryName sourcePattern |> nonNull
            let pattern = Path.GetFileName sourcePattern |> nonNull

            for resource in Directory.GetFiles(parent, pattern) do
                copySourceFile root directory (repositoryRelative resource)

        let lockfile =
            Path.Combine(Path.GetDirectoryName relative |> nonNull, "packages.lock.json")

        copySourceFile root directory lockfile

        for reference in includes "ProjectReference" project do
            copyProject root directory visited (repositoryRelative reference)

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
        ] do
        copySourceFile root directory name

    copyProject
        root
        directory
        (HashSet<string>(StringComparer.Ordinal))
        "src/ClaimCore.Cli/ClaimCore.Cli.fsproj"

    copyNotices root directory
    directory

let private changeStructuralVocabulary directory =
    let path =
        Path.Combine(directory, "src/ClaimCore.Contract/Vocabulary/CaseTokens.fs")

    let original = File.ReadAllText path

    let changed =
        original.Replace("\"OPENED\"", "\"SYNTHETIC_OPENED\"", StringComparison.Ordinal)

    Expect.notEqual changed original "The mismatch fixture changes an actual wire enum"
    File.WriteAllText(path, changed)

let private publish (fixture: PublishedFixture.Fixture) directory name =
    let output = Path.Combine(fixture.Directory, name)

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

    FixtureFiles.write fixture.Directory (name + "-build.stdout.log") built.StandardOutput
    |> ignore

    FixtureFiles.write fixture.Directory (name + "-build.stderr.log") built.StandardError
    |> ignore

    Expect.equal built.ExitCode 0 "Compile the isolated client source control; see owned build logs"

    ThirdPartyNotices.qualify fixture.Root output "ClaimCore.Cli" |> ignore
    output

let private changeLocaleText directory =
    let path = Path.Combine(directory, "src/ClaimCore.Cli/Localization/en.json")
    let locale = JsonNode.Parse(File.ReadAllBytes path) |> nonNull
    let original = (locale["cli.text.code"] |> nonNull).GetValue<string>()
    locale["cli.text.code"] <- JsonValue.Create(original + " synthetic locale control")
    File.WriteAllText(path, locale.ToJsonString() + "\n")

let private localeClient (fixture: PublishedFixture.Fixture) directory =
    changeLocaleText directory
    let output = publish fixture directory "locale-client"
    let before = fixture.Administration [ "head" ]
    Expect.equal before.ExitCode 0 "Read head before the locale-only client control"

    ProcessRunner.dotnet
        60_000
        (Path.Combine(output, "claimcore.dll"))
        [ "call" ]
        fixture.Environment
        (Some(CliCalls.frame "context" (JsonObject())))
    |> CliCalls.parse 0
    |> CliCalls.require "SUCCEEDED"
    |> ignore

    let after = fixture.Administration [ "head" ]
    Expect.equal after.ExitCode 0 "Read head after locale-only client control"

    Expect.equal
        after.StandardOutput
        before.StandardOutput
        "Locale-only client reads append nothing"

let qualify (fixture: PublishedFixture.Fixture) =
    let directory = copySource fixture.Root fixture.Directory

    localeClient fixture directory
    changeStructuralVocabulary directory

    let output = publish fixture directory "different-client"
    let before = fixture.Administration [ "head" ]
    Expect.equal before.ExitCode 0 "Read accepted head before the mismatched mutation"

    let authored =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
        |> nonNull

    let input = authored["input"] |> nonNull
    input["operationId"] <- JsonValue.Create(Guid.NewGuid().ToString("D"))
    input["caseReference"] <- JsonValue.Create "SYNTHETIC-CONTRACT-MISMATCH"

    let result =
        ProcessRunner.dotnet
            60_000
            (Path.Combine(output, "claimcore.dll"))
            [ "call" ]
            fixture.Environment
            (Some(CliCalls.frame "operation.execute" input))
        |> CliCalls.parse 3

    let failure = result.GetProperty("result").GetProperty("hostFailure")
    Expect.equal (failure.GetProperty("code").GetString()) "CONTRACT_MISMATCH" "Handshake refusal"

    Expect.equal
        (failure.GetProperty("phase").GetString())
        "NOT_STARTED"
        "Mismatch precedes dispatch"

    let after = fixture.Administration [ "head" ]
    Expect.equal after.ExitCode 0 "Read accepted head after the mismatched mutation"
    Expect.equal after.StandardOutput before.StandardOutput "Contract mismatch appends nothing"
