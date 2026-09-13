[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Join-Path ([IO.Path]::GetTempPath()) ("claimcore-trx-privacy-" + [Guid]::NewGuid().ToString("N"))
$results = Join-Path $root "results"
$project = Join-Path $root "Probe.fsproj"
$source = Join-Path $root "Probe.fs"
$log = Join-Path $root "runner.log"
$canary = "CLAIMANT-TRX-PRIVACY-CANARY"

$projectText = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsTestProject>true</IsTestProject>
    <GenerateProgramFile>false</GenerateProgramFile>
    <EnableExpectoTestingPlatformIntegration>true</EnableExpectoTestingPlatformIntegration>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Probe.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Expecto" Version="11.1.0" />
    <PackageReference Include="FSharp.Core" Version="10.1.401" />
    <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.4.0" />
    <PackageReference Include="Microsoft.Testing.Extensions.VSTestBridge" Version="2.4.0" />
    <PackageReference Include="Microsoft.Testing.Platform.MSBuild" Version="2.4.0" />
    <PackageReference Include="YoloDev.Expecto.TestSdk" Version="0.16.1" />
  </ItemGroup>
</Project>
'@

$sourceText = @"
module ClaimCore.DiagnosticPrivacyProbe

open Expecto

[<Tests>]
let tests =
    testCase "faulted privacy probe" (fun () ->
        let claimant = "$canary"
        Expect.isTrue ([ claimant ] = [ "different" ]) "Synthetic payload-safe mismatch")
"@

try {
    [IO.Directory]::CreateDirectory($root) | Out-Null
    [IO.File]::WriteAllText($project, $projectText, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($source, $sourceText, [Text.UTF8Encoding]::new($false))
    $arguments = @(
        "test", "--project", $project, "--results-directory=$results",
        "--minimum-expected-tests=1", "--zero-tests-policy=strict", "--timeout=5m",
        "--", "--report-trx", "--report-trx-filename=privacy.trx"
    )
    & dotnet @arguments *> $log
    if ($LASTEXITCODE -eq 0) { throw "The faulted diagnostic probe unexpectedly passed." }
    $trx = Join-Path $results "privacy.trx"
    if (-not (Test-Path -LiteralPath $trx -PathType Leaf)) { throw "The faulted diagnostic probe emitted no TRX." }

    $combined = [IO.File]::ReadAllText($trx) + [IO.File]::ReadAllText($log)
    if ($combined.Contains($canary, [StringComparison]::Ordinal)) {
        throw "A structural test failure serialized claimant data."
    }
    [xml] $document = [IO.File]::ReadAllText($trx)
    $namespace = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespace.AddNamespace("t", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
    $failures = @($document.SelectNodes('//t:UnitTestResult[@outcome="Failed"]', $namespace))
    if ($failures.Count -ne 1) { throw "The diagnostic probe did not record one intentional failure." }
    Write-Host "Faulted TRX diagnostic privacy control passed."
}
finally {
    if (Test-Path -LiteralPath $root -PathType Container) { [IO.Directory]::Delete($root, $true) }
}
