module ClaimCore.DocsTests.ManifestTests

open System
open System.IO
open System.Text
open Expecto
open ClaimCore.Docs
open ClaimCore.DocsTests.Fixtures

let private digest = String.replicate 64 "a"

let private tools =
    {
        DotnetSdk = "10.0.401"
        Node = Some "26.8.2"
        Npm = Some "11.19.1"
        PostgreSql = Some "postgres:18.6@sha256:test"
        OperatingSystem = "test-os"
        Architecture = "test-arch"
    }

let private treeManifest (repository: TempRepository) =
    let output = Path.Combine(repository.Path, "artifacts/output")
    Directory.CreateDirectory(Path.Combine(output, "nested")) |> ignore
    File.WriteAllText(Path.Combine(output, "z.txt"), "z")
    File.WriteAllText(Path.Combine(output, "nested/a.txt"), "alpha")
    output, PublishManifest.create "publish-cli" digest digest tools output |> requireOk

let private publishTests =
    testList
        "publish tree manifests"
        [
            testCase "round-trips a sorted deterministic tree manifest"
            <| fun _ ->
                use repository = new TempRepository()
                let output, manifest = treeManifest repository
                let bytes = PublishManifestWriter.serialize manifest
                let parsed = PublishManifest.parse bytes |> requireOk

                Expect.equal parsed manifest "Typed manifest round-trip"

                Expect.equal
                    (parsed.Files |> List.map _.Path)
                    [ "nested/a.txt"; "z.txt" ]
                    "Ordinal paths"

                Expect.equal (PublishManifest.verifyTree output parsed) (Ok()) "Tree revalidation"

            testCase "detects output mutation after manifest creation"
            <| fun _ ->
                use repository = new TempRepository()
                let output, manifest = treeManifest repository
                File.WriteAllText(Path.Combine(output, "z.txt"), "changed")
                PublishManifest.verifyTree output manifest |> requireError |> ignore

            testCase "rejects symlinks in output trees"
            <| fun _ ->
                use repository = new TempRepository()
                let output = Path.Combine(repository.Path, "artifacts/output")
                Directory.CreateDirectory(output) |> ignore
                let outside = repository.Write("outside.txt", "outside")
                File.CreateSymbolicLink(Path.Combine(output, "linked.txt"), outside) |> ignore

                PublishManifest.create "publish-cli" digest digest tools output
                |> requireError
                |> ignore

            testCase "rejects unsafe paths and duplicate JSON properties"
            <| fun _ ->
                let invalid =
                    "{\"schemaVersion\":1,\"schemaVersion\":1,\"stageId\":\"x\",\"sourceSha256\":\""
                    + digest
                    + "\",\"locksSha256\":\""
                    + digest
                    + "\",\"toolchain\":{},\"files\":[],\"treeSha256\":\""
                    + digest
                    + "\"}"

                invalid
                |> Encoding.UTF8.GetBytes
                |> PublishManifest.parse
                |> requireError
                |> ignore
        ]

let private nativeLibraryRequirements () =
    let native = OutputRequirement.NativeHostSecurityLibrary

    for stageId in [ "publish-cli"; "publish-database"; "publish-web" ] do
        let stage = Stages.tryFind stageId |> Option.get
        Expect.contains stage.RequiredOutputs native "Every runtime publish requires the shim"

    let file path =
        {
            Path = path
            Length = 1L
            Sha256 = digest
        }

    let mac = [ file "libclaimcore_hostsecurity_native.dylib" ]
    let linux = [ file "libclaimcore_hostsecurity_native.so" ]
    Expect.isFalse (Stages.satisfies "macos" [] native) "Missing macOS shim refuses"
    Expect.isTrue (Stages.satisfies "macos" mac native) "Exact macOS shim admits"
    Expect.isFalse (Stages.satisfies "macos" linux native) "Linux shim is not macOS"
    Expect.isFalse (Stages.satisfies "linux" [] native) "Missing Linux shim refuses"
    Expect.isTrue (Stages.satisfies "linux" linux native) "Exact Linux shim admits"
    Expect.isFalse (Stages.satisfies "linux" mac native) "macOS shim is not Linux"
    Expect.isTrue (Stages.satisfies "windows" [] native) "Windows has no native shim"
    Expect.isFalse (Stages.satisfies "windows" linux native) "Windows rejects stale shim"

let private containerImageRequirements () =
    let containerSbom = Stages.tryFind "container-sbom" |> Option.get

    Expect.equal
        containerSbom.Procedure
        [ "bash"; "eng/Check-PostgresImageAssurance.sh"; "sbom" ]
        "Container SBOM procedure selects each child of the pinned index"

    Expect.equal
        containerSbom.RequiredOutputs
        [
            OutputRequirement.Exact "postgresql-linux-amd64.cdx.json"
            OutputRequirement.Exact "postgresql-linux-arm64.cdx.json"
        ]
        "Both architecture SBOMs are required"

    Expect.equal
        (Stages.tryFind "container-vulnerability-scan" |> Option.get).Procedure
        [ "bash"; "eng/Check-PostgresImageAssurance.sh"; "scan" ]
        "The vulnerability gate scans each child of the pinned index"

let private stageRegistryTests =
    testList
        "stage registry"
        [
            testCase "registers each required stage exactly once without aggregate quality"
            <| fun _ ->
                let ids = Stages.definitions |> List.map _.Id
                Expect.equal ids.Length (ids |> Set.ofList |> Set.count) "No duplicate stage IDs"
                Expect.isFalse (ids |> List.contains "quality") "No ceremonial aggregate gate"

                [
                    "restore-dotnet"
                    "fsharplint"
                    "convergence-assurance"
                    "convergence-assurance-negative-controls"
                    "npm-audit"
                    "docs-write-idempotence"
                    "fresh-baseline-qualification"
                    "container-image-assurance-negative-controls"
                    "docker-cleanup-assurance"
                    "secret-scan-artifacts"
                    "publish-cli"
                    "browser-webkit"
                ]
                |> List.iter (fun id -> Expect.contains ids id "Conjunctive stage")

                Expect.equal
                    (Stages.tryFind "convergence-assurance" |> Option.get).Procedure
                    [ "pwsh"; "Check-ConvergenceAssurance.ps1" ]
                    "Convergence assurance procedure"

                Expect.equal
                    (Stages.tryFind "convergence-assurance-negative-controls" |> Option.get)
                        .Procedure
                    [ "pwsh"; "Test-ConvergenceAssurancePolicy.ps1" ]
                    "Convergence negative-control procedure"

                Expect.equal
                    (Stages.tryFind "docker-cleanup-assurance" |> Option.get).Procedure
                    [ "bash"; "eng/Test-LabeledTestContainerCleanup.sh" ]
                    "Exact-label Docker cleanup must run its live and negative controls"

                containerImageRequirements ()

            testCase "portable publication records still require Linux CI evidence"
            <| fun _ ->
                let publication = Stages.tryFind "publish-cli" |> Option.get

                Expect.sequenceEqual
                    publication.AllowedPlatforms
                    [ "linux"; "macos"; "windows" ]
                    "Local producers"

                Expect.equal publication.EvidencePlatform (Some "linux") "CI qualification platform"

            testCase
                "native private-file library is required in each supported publish tree"
                nativeLibraryRequirements

        ]

let private rejectsChangedProcedure () =
    use repository = new TempRepository()
    let output, publish = treeManifest repository
    let definition = Stages.tryFind "publish-cli" |> Option.get

    let manifest =
        {
            SchemaVersion = 2
            StageId = "publish-cli"
            RunId = "run-1"
            Attempt = 1
            Outcome = "success"
            StartedUtc = DateTimeOffset.Parse("2026-09-09T00:00:00.0000000+00:00")
            FinishedUtc = DateTimeOffset.Parse("2026-09-09T00:00:01.0000000+00:00")
            Platform = "linux"
            Procedure = [ "invented" ]
            RequiredOutputs = definition.RequiredOutputs |> List.map Stages.displayRequirement
            OutputRoot = Repository.relativePath repository.Root output
            Output = publish
        }

    let bytes = StageManifestFormat.serialize manifest
    let parsed = bytes |> StageManifestFormat.parse |> requireOk

    StageManifestFormat.validate definition "run-1" 1 parsed
    |> requireError
    |> ignore

    let serialized = Encoding.UTF8.GetString(bytes)

    serialized.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1")
    |> Encoding.UTF8.GetBytes
    |> StageManifestFormat.parse
    |> requireError
    |> ignore

    serialized.Replace("\"procedure\"", "\"command\"")
    |> Encoding.UTF8.GetBytes
    |> StageManifestFormat.parse
    |> requireError
    |> ignore

let private refusesTrackedReportDestinations () =
    use repository = new TempRepository()

    AtomicFile.write repository.Root "docs/report.json" [| 1uy |]
    |> requireError
    |> ignore

    File.WriteAllText(Path.Combine(repository.Path, "artifacts"), "blocked")

    let original =
        Diagnostic.create DiagnosticCode.InvalidMarkdown "Original documentation error."

    let source =
        {
            GitRevision = None
            State = "unversioned"
            ContentSha256 = digest
            LocksSha256 = digest
        }

    let reportWrite =
        DocumentationReport.write repository.Root "check" "failed" source [] 0 0 [] [ original ]

    let combined: Result<unit, Diagnostic list> =
        DocumentationCommands.includeReportWrite "check" (Error [ original ]) reportWrite

    let diagnostics = combined |> requireError
    Expect.equal diagnostics.Length 2 "Report failure is appended"
    Expect.equal diagnostics.Head.Message original.Message "Original failure is retained"
    Expect.equal diagnostics[1].Code DiagnosticCode.Invocation "Report failure is classified"

    DocumentationCommands.includeReportWrite "check" (Ok()) reportWrite
    |> requireError
    |> List.exactlyOne
    |> fun diagnostic ->
        Expect.stringContains
            diagnostic.Message
            "report could not be written"
            "A successful check fails closed when its report is absent"

let private stageManifestTests =
    testList
        "stage manifests"
        [
            testCase "stage format rejects a changed registered procedure" rejectsChangedProcedure
            testCase
                "atomic report writer refuses tracked destinations"
                refusesTrackedReportDestinations
        ]

let private stageTests =
    testList "stage registry and manifests" [ stageRegistryTests; stageManifestTests ]

let tests = testList "Evidence manifests" [ publishTests; stageTests ]
