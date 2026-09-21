module ClaimCore.DocsTests.ArchitectureReportTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Docs
open ClaimCore.DocsTests.Fixtures

/// The production validator takes its expected set from the manifest this tool embedded, so the
/// fixture uses that same set rather than restating one.
let private required =
    ArchitectureManifest.current.Value |> ArchitectureManifest.inTier "product"

let private names = required |> Set.toList |> List.sort

let private validate bytes =
    ArchitectureInspectionReport.validateBytes required bytes

let private graphBytes names edges =
    JsonSerializer.SerializeToUtf8Bytes(
        {|
            format = "claimcore-architecture-inspection"
            formatVersion = 1
            configuration = "Debug"
            assemblies = names |> List.map (fun name -> {| name = name; inspectedTypes = 1 |})
            edges =
                edges
                |> List.map (fun (source, target) -> {| source = source; target = target |})
        |}
    )

let private valid = graphBytes names [ "ClaimCore.Application", "ClaimCore.Domain" ]

let private reportShape =
    testCase "accepts a bounded sorted ArchUnitNET model report" (fun () ->
        Expect.equal (validate valid) (Ok()) "Exact graph"

        Expect.isError (validate (Array.zeroCreate (16 * 1024 + 1))) "Oversized report is refused")

let private reportInventory =
    testCase "rejects duplicate, incomplete, or unknown architecture inventory" (fun () ->
        let invalid =
            [
                graphBytes (List.rev names) [ "ClaimCore.Application", "ClaimCore.Domain" ]
                graphBytes (List.tail names) [ "ClaimCore.Application", "ClaimCore.Domain" ]
                graphBytes names [ "ClaimCore.Application", "ClaimCore.Unknown" ]
                graphBytes
                    names
                    [ "ClaimCore.Beta", "ClaimCore.Alpha"; "ClaimCore.Beta", "ClaimCore.Alpha" ]
            ]

        for bytes in invalid do
            Expect.isError (validate bytes) "Unqualified inventory is refused"

        let duplicated =
            Encoding.UTF8
                .GetString(valid)
                .Replace(
                    "\"formatVersion\":1",
                    "\"formatVersion\":1,\"formatVersion\":1",
                    StringComparison.Ordinal
                )

        Expect.isError
            (validate (Encoding.UTF8.GetBytes duplicated))
            "Duplicate JSON properties are refused")

let private stage runId files =
    {
        SchemaVersion = 1
        StageId = "architecture-linux"
        RunId = runId
        Attempt = 1
        Outcome = "success"
        StartedUtc = DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)
        FinishedUtc = DateTimeOffset(2026, 9, 14, 0, 0, 1, TimeSpan.Zero)
        Platform = "linux"
        Procedure = [ "dotnet"; "test" ]
        RequiredOutputs = [ "exact:architecture-report.json" ]
        OutputRoot = "artifacts/test-results/architecture-linux"
        Output =
            {
                SchemaVersion = 1
                StageId = "architecture-linux"
                SourceSha256 = String.replicate 64 "a"
                LocksSha256 = String.replicate 64 "b"
                Toolchain =
                    {
                        DotnetSdk = "10.0.401"
                        Node = None
                        Npm = None
                        PostgreSql = None
                        OperatingSystem = "test"
                        Architecture = "test"
                    }
                Files = files
                TreeSha256 = String.replicate 64 "c"
            }
    }

let private downloadedReport =
    testCase "refuses missing or tampered downloaded architecture evidence" (fun () ->
        use repository = new TempRepository()

        let relative =
            "artifacts/evidence-inputs/architecture-linux/architecture-report.json"

        let path = repository.WriteBytes(relative, valid)

        let file =
            {
                Path = ArchitectureInspectionReport.fileName
                Length = int64 valid.Length
                Sha256 = Repository.sha256Bytes valid
            }

        let producer = stage "synthetic-run" [ file ]

        Expect.equal
            (ArchitectureInspectionReport.validateDownloaded repository.Root producer)
            (Ok())
            "Same producer bytes validate"

        Expect.isError
            (ArchitectureInspectionReport.validateDownloaded
                repository.Root
                (stage "synthetic-run" []))
            "Missing producer entry is refused"

        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("changed"))

        Expect.isError
            (ArchitectureInspectionReport.validateDownloaded repository.Root producer)
            "Tampered downloaded bytes are refused"

        File.Delete(path)

        Expect.isError
            (ArchitectureInspectionReport.validateDownloaded repository.Root producer)
            "Missing download is refused")

let private manifestCurrency =
    testCase
        "a manifest that drifted from the compiled tool fails the documentation check"
        (fun () ->
            use repository = new TempRepository()

            Expect.isError
                (ArchitectureManifest.requireCurrent repository.Root)
                "A repository without the manifest cannot be current"

            repository.Write(ArchitectureManifest.fileName, "{\"version\":1,\"components\":[]}")
            |> ignore

            Expect.isError
                (ArchitectureManifest.requireCurrent repository.Root)
                "A manifest that differs from the embedded copy is refused"

            Expect.isError
                (ArchitectureManifest.parse "{\"version\":2,\"components\":[]}")
                "Version"

            Expect.isError (ArchitectureManifest.parse "not json") "Malformed manifest"
            Expect.isError (ArchitectureManifest.parse "{\"version\":1,\"components\":[]}") "Empty"

            Expect.isNonEmpty required "The embedded manifest classifies product components")

let private stageRequirement =
    testCase "all three architecture stages require the inspected graph artifact" (fun () ->
        for platform in [ "linux"; "macos"; "windows" ] do
            let entry = Stages.tryFind ("architecture-" + platform) |> Option.get

            Expect.contains
                entry.RequiredOutputs
                (OutputRequirement.Exact ArchitectureInspectionReport.fileName)
                "No platform may omit the report")

let tests =
    testList
        "ArchUnitNET report evidence"
        [
            reportShape
            reportInventory
            downloadedReport
            manifestCurrency
            stageRequirement
        ]
