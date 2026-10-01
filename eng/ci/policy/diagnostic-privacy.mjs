// A failing test must not serialize the data it compared. This builds a one-test probe project whose
// assertion fails on a synthetic claimant value, runs it through the real test runner with TRX
// reporting, and requires that neither the report nor the runner output contains the value.
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { descendantsNamed, parseXml } from "../suites/xml.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const canary = "CLAIMANT-TRX-PRIVACY-CANARY";
const probeTests = 1;
const packages = [
  "Expecto",
  "FSharp.Core",
  "Microsoft.Testing.Extensions.TrxReport",
  "Microsoft.Testing.Extensions.VSTestBridge",
  "Microsoft.Testing.Platform.MSBuild",
  "YoloDev.Expecto.TestSdk",
];

/**
 * @param {string} name
 * @returns {string} The version Directory.Packages.props pins.
 */
function pinnedVersion(name) {
  const props = readFileSync(join(root, "Directory.Packages.props"), "utf8");
  const match = new RegExp(
    `<PackageVersion Include="${name.replaceAll(".", "\\.")}" Version="([^"]+)"`,
    "u",
  ).exec(props);
  if (!match?.[1]) {
    throw new Error(`Directory.Packages.props does not pin ${name}.`);
  }
  return match[1];
}

/** @returns {string} */
function probeProject() {
  const references = packages
    .map((name) => `    <PackageReference Include="${name}" Version="${pinnedVersion(name)}" />`)
    .join("\n");
  return `<Project Sdk="Microsoft.NET.Sdk">
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
${references}
  </ItemGroup>
</Project>
`;
}

const probeSource = `module ClaimCore.DiagnosticPrivacyProbe

open Expecto

[<Tests>]
let tests =
    testCase "faulted privacy probe" (fun () ->
        let claimant = "${canary}"
        Expect.isTrue ([ claimant ] = [ "different" ]) "Synthetic payload-safe mismatch")
`;

/** Run the probe and fail on any leak. */
function main() {
  const scratch = mkdtempSync(join(tmpdir(), "claimcore-trx-privacy-"));
  try {
    const project = join(scratch, "Probe.fsproj");
    writeFileSync(project, probeProject());
    writeFileSync(join(scratch, "Probe.fs"), probeSource);
    const results = join(scratch, "results");
    const run = spawnSync(
      "dotnet",
      [
        "test",
        "--project",
        project,
        `--results-directory=${results}`,
        `--minimum-expected-tests=${probeTests}`,
        "--zero-tests-policy=strict",
        "--timeout=5m",
        "--",
        "--report-trx",
        "--report-trx-filename=privacy.trx",
      ],
      { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 },
    );
    if (run.status === 0) {
      throw new Error("The faulted diagnostic probe unexpectedly passed.");
    }
    const trx = join(results, "privacy.trx");
    if (!existsSync(trx)) {
      throw new Error("The faulted diagnostic probe emitted no TRX.");
    }
    const report = readFileSync(trx, "utf8");
    if ((report + run.stdout + run.stderr).includes(canary)) {
      throw new Error("A structural test failure serialized claimant data.");
    }
    const failures = descendantsNamed(parseXml(report), "UnitTestResult").filter(
      (result) => result.attributes["outcome"] === "Failed",
    );
    if (failures.length !== 1) {
      throw new Error("The diagnostic probe did not record one intentional failure.");
    }
    process.stdout.write("Faulted TRX diagnostic privacy control passed.\n");
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

try {
  main();
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
}
