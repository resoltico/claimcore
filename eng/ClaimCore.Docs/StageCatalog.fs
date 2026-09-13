namespace ClaimCore.Docs

[<RequireQualifiedAccess>]
module StageCatalog =
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

    let private bootstrap =
        [
            gate "restore-dotnet" [ "dotnet"; "restore"; "--locked-mode" ]
            gate "restore-tools" [ "dotnet"; "tool"; "restore" ]
            gate "restore-frontend" [ "npm"; "ci" ]
            linux
                "build-release"
                [ "dotnet"; "build"; "Release" ]
                [ OutputRequirement.Suffix ".dll" ]
        ]

    let private sourceQuality =
        [
            gate "fantomas" [ "bash"; "eng/Check-Fantomas.sh" ]
            gate "fsharplint" [ "bash"; "eng/Check-FSharpLint.sh" ]
            gate "analyzer-suppressions" [ "pwsh"; "Check-AnalyzerSuppressions.ps1" ]
            gate
                "analyzer-suppression-negative-controls"
                [ "pwsh"; "Test-AnalyzerSuppressionPolicy.ps1" ]
            gate "actionlint" [ "actionlint" ]
            gate "shellcheck" [ "shellcheck" ]
            gate "compose-config" [ "docker"; "compose"; "config" ]
            gate "compose-health" [ "docker"; "compose"; "up"; "--wait" ]
            gate
                "secret-scan-source"
                [
                    "pwsh"
                    "Check-GitIgnorePolicy.ps1"
                    "Test-SourceSecretScanPolicy.ps1"
                    "Scan-SourceSecrets.ps1"
                    "Check-ArtifactUploadPolicy.ps1"
                    "Test-ArtifactUploadPolicy.ps1"
                    "Test-ArtifactSecretScanPolicy.ps1"
                    "gitleaks"
                    "source"
                ]
            gate "secret-scan-artifacts" [ "gitleaks"; "artifacts" ]
            gate "sensitive-output-negative-controls" [ "pwsh"; "Test-SensitiveOutputPolicy.ps1" ]
            gate "coverage-input-negative-controls" [ "pwsh"; "Test-CoverageInputPolicy.ps1" ]
            gate "coverage-floor-negative-controls" [ "pwsh"; "Test-MergedCoveragePolicy.ps1" ]
            gate "property-seed-negative-controls" [ "pwsh"; "Test-PropertySeedPolicy.ps1" ]
            gate "test-diagnostic-negative-controls" [ "pwsh"; "Test-TestDiagnosticPrivacy.ps1" ]
        ]

    let private dependencies =
        [
            gate "dependency-currency" [ "pwsh"; "Check-DependencyCurrency.ps1" ]
            gate "nuget-audit" [ "dotnet"; "restore"; "audit" ]
            gate "npm-audit" [ "npm"; "audit" ]
            gate "npm-signatures" [ "npm"; "audit"; "signatures" ]
            gate "dependency-licenses" [ "npm"; "run"; "licenses:check" ]
            linux "sbom" [ "npm"; "run"; "sbom" ] [ OutputRequirement.Suffix ".cdx.json" ]
            linux
                "container-sbom"
                [ "trivy"; "image"; "cyclonedx" ]
                [ OutputRequirement.Suffix ".cdx.json" ]
            gate "container-vulnerability-scan" [ "trivy"; "image" ]
        ]

    let private frontend =
        [
            gate "frontend-format" [ "npm"; "run"; "format:check" ]
            gate "frontend-types" [ "npm"; "run"; "typecheck" ]
            gate "frontend-eslint" [ "npm"; "run"; "lint" ]
            gate "frontend-stylelint" [ "npm"; "run"; "lint:styles" ]
            gate "frontend-dead-code" [ "npm"; "run"; "dead-code" ]
            gate "frontend-contract-inventory" [ "npm"; "run"; "contract:check" ]
            linux
                "frontend-unit"
                [ "npm"; "run"; "test:unit" ]
                [
                    OutputRequirement.Exact "vitest-summary.json"
                    OutputRequirement.Suffix "coverage-summary.json"
                ]
            linux
                "frontend-build"
                [ "npm"; "run"; "build" ]
                [ OutputRequirement.Exact "dist/index.html" ]
        ]

    let private documentation =
        [
            gate "docs-check" [ "claimcore-docs"; "check" ]
            gate "docs-write-idempotence" [ "claimcore-docs"; "write-twice" ]
        ]

    let private behavior =
        [
            linux
                "recovery-qualification"
                [ "dotnet"; "test"; "ClaimCore.RecoveryQualificationTests" ]
                [ OutputRequirement.Suffix "ClaimCore.RecoveryQualificationTests.trx" ]
            linux
                "concurrency-qualification"
                [ "dotnet"; "test"; "ClaimCore.ConcurrencyQualificationTests" ]
                [ OutputRequirement.Suffix "ClaimCore.ConcurrencyQualificationTests.trx" ]
            linux
                "migration-upgrade-qualification"
                [ "dotnet"; "test"; "ClaimCore.MigrationQualificationTests" ]
                [ OutputRequirement.Suffix "ClaimCore.MigrationQualificationTests.trx" ]
            linux
                "coverage"
                [ "reportgenerator"; "coverage" ]
                [ OutputRequirement.Exact "Cobertura.xml" ]
        ]

    let private publications =
        [
            portable
                "publish-cli"
                [ "dotnet"; "publish"; "ClaimCore.Cli" ]
                [
                    OutputRequirement.Exact "ClaimCore.Cli.dll"
                    OutputRequirement.NativeHostSecurityLibrary
                    OutputRequirement.Exact "LICENSE"
                    OutputRequirement.Exact "ClaimCore.Cli.cdx.json"
                    OutputRequirement.Exact "THIRD-PARTY-NOTICES.txt"
                ]
            portable
                "publish-database"
                [ "dotnet"; "publish"; "ClaimCore.Database" ]
                [
                    OutputRequirement.Exact "ClaimCore.Database.dll"
                    OutputRequirement.NativeHostSecurityLibrary
                    OutputRequirement.Exact "LICENSE"
                    OutputRequirement.Exact "ClaimCore.Database.cdx.json"
                    OutputRequirement.Exact "THIRD-PARTY-NOTICES.txt"
                ]
            portable
                "publish-web"
                [ "dotnet"; "publish"; "ClaimCore.Web" ]
                [
                    OutputRequirement.Exact "ClaimCore.Web.dll"
                    OutputRequirement.NativeHostSecurityLibrary
                    OutputRequirement.Exact "LICENSE"
                    OutputRequirement.Exact "ClaimCore.Web.cdx.json"
                    OutputRequirement.Exact "ClaimCore.Web.frontend.cdx.json"
                    OutputRequirement.Exact "THIRD-PARTY-NOTICES.txt"
                    OutputRequirement.Exact "wwwroot/index.html"
                    OutputRequirement.Exact "wwwroot/THIRD-PARTY-NOTICES.txt"
                ]
            gate "publish-tree-verification" [ "claimcore-docs"; "verify-publish-manifest" ]
        ]

    let private unitTests =
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

    let private webTests =
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

    let private docsTests =
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

    let private persistenceTests =
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

    let private browser engine =
        linux
            ("browser-" + engine)
            [ "coverlet.console"; "published-host"; "playwright"; engine ]
            [ OutputRequirement.Exact(engine + ".json") ]

    let private browsers = [ browser "chromium"; browser "firefox"; browser "webkit" ]

    let definitions =
        bootstrap
        @ sourceQuality
        @ dependencies
        @ frontend
        @ documentation
        @ behavior
        @ publications
        @ unitTests
        @ webTests
        @ docsTests
        @ persistenceTests
        @ browsers
        @ [
            linux
                "evidence-inputs"
                [ "claimcore-docs"; "evidence-inputs" ]
                [ OutputRequirement.Suffix ".trx" ]
        ]
