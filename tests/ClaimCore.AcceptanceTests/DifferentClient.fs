module internal ClaimCore.AcceptanceTests.DifferentClient

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Xml.Linq
open Expecto
open ClaimCore.Wire
open ClaimCore.ContractGeneration

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

let private differentInputs (artifacts: Artifact list) =
    let manifest =
        artifacts
        |> List.find (fun artifact -> artifact.Name = "contract-manifest.json")

    use document = JsonDocument.Parse(ReadOnlyMemory<byte> manifest.Bytes)

    let names =
        document.RootElement.GetProperty("inputs").EnumerateArray()
        |> Seq.map (fun row -> row.GetProperty("name").GetString() |> nonNull)
        |> Set.ofSeq

    artifacts
    |> List.filter (fun artifact -> Set.contains artifact.Name names)
    |> List.map (fun artifact ->
        if artifact.Name <> "locale.en.ui.json" then
            artifact
        else
            let locale = JsonNode.Parse artifact.Bytes |> nonNull
            let original = (locale["ui.loading"] |> nonNull).GetValue<string>()
            locale["ui.loading"] <- JsonValue.Create(original + " fixture")

            { artifact with
                Bytes = Encoding.UTF8.GetBytes(locale.ToJsonString() + "\n")
            })

let private differentArtifacts () =
    let original = ContractArtifacts.all ()
    let inputs = differentInputs original
    let manifest = ContractIdentity.manifest inputs
    let identity = ContractIdentity.digest manifest
    Expect.notEqual identity ConsumerContract.identity "Locale-only changes alter consumer identity"

    let changed =
        inputs |> List.map (fun artifact -> artifact.Name, artifact.Bytes) |> Map.ofList

    let wire = original |> List.find (fun artifact -> artifact.Name = "openapi.json")

    Expect.equal
        changed["openapi.json"]
        wire.Bytes
        "The different client retains identical wire shape"

    original
    |> List.map (fun artifact ->
        let bytes =
            match artifact.Name with
            | "contract-manifest.json" -> manifest
            | "contract-identity.txt" -> Encoding.UTF8.GetBytes(identity + "\n")
            | "contract-identity.fs" ->
                Encoding.UTF8.GetBytes(
                    Encoding.UTF8
                        .GetString(artifact.Bytes)
                        .Replace(ConsumerContract.identity, identity)
                )
            | "client.ts" -> TypeScript.source identity
            | name -> Map.tryFind name changed |> Option.defaultValue artifact.Bytes

        { artifact with Bytes = bytes })

let private publish (fixture: PublishedFixture.Fixture) directory =
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
        "Compile a client with a different consumer identity; see owned build logs"

    ThirdPartyNotices.qualify fixture.Root output "ClaimCore.Cli" |> ignore
    output

let qualify (fixture: PublishedFixture.Fixture) =
    let directory = copySource fixture.Root fixture.Directory

    differentArtifacts ()
    |> ArtifactPublication.publish (Path.Combine(directory, "contracts"))

    let output = publish fixture directory
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
            (Path.Combine(output, "ClaimCore.Cli.dll"))
            [ "call" ]
            fixture.Environment
            (Some(CliCalls.frame "case.execute" input))
        |> CliCalls.parse 3

    Expect.equal
        (result.GetProperty("kind").GetString())
        "serviceFailure"
        "Different client is refused"

    let failure = result.GetProperty "failure"

    Expect.equal
        (failure.GetProperty("code").GetString())
        "CONTRACT_MISMATCH"
        "Bundle handshake refusal"

    Expect.equal
        (failure.GetProperty("executionPhase").GetString())
        "NOT_STARTED"
        "Mismatch precedes dispatch"

    let after = fixture.Administration [ "head" ]
    Expect.equal after.ExitCode 0 "Read accepted head after the mismatched mutation"
    Expect.equal after.StandardOutput before.StandardOutput "Contract mismatch appends nothing"
