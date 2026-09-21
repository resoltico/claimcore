namespace ClaimCore.Docs

/// Stages that execute a registered test suite. Each one binds the suite's TRX result, and
/// the architecture suite additionally binds its observed graph report.
[<RequireQualifiedAccess>]
module TestSuiteStages =
    let private stage id platform procedure outputs =
        {
            Id = id
            AllowedPlatforms = [ platform ]
            EvidencePlatform = Some platform
            Procedure = procedure
            RequiredOutputs = outputs
        }

    let private linux id procedure outputs = stage id "linux" procedure outputs

    let private gate id procedure = linux id procedure []

    let private portable id procedure outputs =
        {
            Id = id
            AllowedPlatforms = [ "linux"; "macos"; "windows" ]
            EvidencePlatform = Some "linux"
            Procedure = procedure
            RequiredOutputs = outputs
        }

    let unitTests =
        [
            stage
                "unit-linux"
                "linux"
                [ "dotnet"; "test"; "ClaimCore.Tests" ]
                [ OutputRequirement.Suffix "ClaimCore.Tests.trx" ]
            stage
                "unit-macos"
                "macos"
                [ "dotnet"; "test"; "ClaimCore.Tests" ]
                [ OutputRequirement.Suffix "ClaimCore.Tests.trx" ]
            stage
                "unit-windows"
                "windows"
                [ "dotnet"; "test"; "ClaimCore.Tests" ]
                [ OutputRequirement.Suffix "ClaimCore.Tests.trx" ]
        ]

    let fuzzTests =
        [ "linux"; "macos"; "windows" ]
        |> List.map (fun platform ->
            stage
                ("fuzz-" + platform)
                platform
                [ "dotnet"; "test"; "ClaimCore.FuzzQualificationTests" ]
                [ OutputRequirement.Suffix "ClaimCore.FuzzQualificationTests.trx" ])

    let architectureTests =
        [ "linux"; "macos"; "windows" ]
        |> List.map (fun platform ->
            stage
                ("architecture-" + platform)
                platform
                [ "dotnet"; "test"; "ClaimCore.ArchitectureTests"; "Debug" ]
                [
                    OutputRequirement.Suffix "ClaimCore.ArchitectureTests.trx"
                    OutputRequirement.Exact ArchitectureInspectionReport.fileName
                ])

    let webTests =
        [
            stage
                "web-linux"
                "linux"
                [ "dotnet"; "test"; "ClaimCore.WebTests" ]
                [ OutputRequirement.Suffix "ClaimCore.WebTests.trx" ]
            stage
                "web-macos"
                "macos"
                [ "dotnet"; "test"; "ClaimCore.WebTests" ]
                [ OutputRequirement.Suffix "ClaimCore.WebTests.trx" ]
            stage
                "web-windows"
                "windows"
                [ "dotnet"; "test"; "ClaimCore.WebTests" ]
                [ OutputRequirement.Suffix "ClaimCore.WebTests.trx" ]
        ]

    let docsTests =
        [
            stage
                "docs-linux"
                "linux"
                [ "dotnet"; "test"; "ClaimCore.DocsTests" ]
                [ OutputRequirement.Suffix "ClaimCore.DocsTests.trx" ]
            stage
                "docs-macos"
                "macos"
                [ "dotnet"; "test"; "ClaimCore.DocsTests" ]
                [ OutputRequirement.Suffix "ClaimCore.DocsTests.trx" ]
            stage
                "docs-windows"
                "windows"
                [ "dotnet"; "test"; "ClaimCore.DocsTests" ]
                [ OutputRequirement.Suffix "ClaimCore.DocsTests.trx" ]
        ]

    let persistenceTests =
        [
            stage
                "integration-linux"
                "linux"
                [ "dotnet"; "test"; "ClaimCore.IntegrationTests" ]
                [ OutputRequirement.Suffix "ClaimCore.IntegrationTests.trx" ]
            stage
                "acceptance-linux"
                "linux"
                [ "dotnet"; "test"; "ClaimCore.AcceptanceTests" ]
                [ OutputRequirement.Suffix "ClaimCore.AcceptanceTests.trx" ]
        ]

    let definitions =
        unitTests
        @ fuzzTests
        @ architectureTests
        @ webTests
        @ docsTests
        @ persistenceTests
