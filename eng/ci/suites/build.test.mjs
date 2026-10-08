import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { buildGroups, buildSuites, solutionText } from "./build.mjs";
import { executable } from "../executable.mjs";
import { publicationBuildSuite } from "../run-publication.mjs";

test("publication builds share one Release graph for all three applications and the registered harness", () => {
  const root = resolve(import.meta.dirname, "../../..");
  assert.deepEqual(buildGroups([publicationBuildSuite(root)]), [
    {
      configuration: "Release",
      msbuild: [],
      projects: [
        "src/ClaimCore.Cli/ClaimCore.Cli.fsproj",
        "src/ClaimCore.Database/ClaimCore.Database.fsproj",
        "src/ClaimCore.Web/ClaimCore.Web.fsproj",
        "tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj",
      ],
    },
  ]);
});

test("publication builds preserve registered harness prerequisites and refuse missing or incompatible configuration", () => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-publication-build-plan-"));
  try {
    mkdirSync(join(root, "config"));
    const original = /** @type {{suites:import("./registry.mjs").Suite[]}} */ (
      JSON.parse(readFileSync(new URL("../../../config/test-suites.json", import.meta.url), "utf8"))
    );
    const suite = original.suites.find((item) => item.id === "acceptance");
    assert.ok(suite);
    const write = () =>
      writeFileSync(join(root, "config/test-suites.json"), JSON.stringify(original));
    const prerequisite = "tests/ClaimCore.Tests/ClaimCore.Tests.fsproj";
    suite.build = [prerequisite];
    write();
    assert.ok(buildGroups([publicationBuildSuite(root)])[0]?.projects.includes(prerequisite));
    suite.configuration = "Debug";
    write();
    assert.throws(() => publicationBuildSuite(root));
    original.suites = original.suites.filter((item) => item.id !== "acceptance");
    write();
    assert.throws(() => publicationBuildSuite(root), /registered acceptance/u);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

/** @param {string} root */
function configurationFixture(root) {
  const pin = readFileSync(new URL("../../../global.json", import.meta.url), "utf8");
  const {
    sdk: { version },
  } = JSON.parse(pin);
  const listing = spawnSync(executable("dotnet"), ["--list-sdks"], { encoding: "utf8" });
  assert.equal(listing.status, 0);
  const sdk = listing.stdout.split(/\r?\n/u).find((line) => line.startsWith(`${version} [`));
  assert.ok(sdk, "The pinned native SDK is required for the configuration regression.");
  const core = join(sdk.slice(version.length + 2, -1), version, "FSharp", "FSharp.Core.dll");
  assert.ok(existsSync(core));
  writeFileSync(join(root, "global.json"), pin);
  for (const project of ["dependency", "selected"]) {
    const directory = join(root, "tests", project);
    mkdirSync(directory, { recursive: true });
    const reference =
      project === "selected"
        ? '<ProjectReference Include="../dependency/dependency.fsproj" />'
        : "";
    const hint = core.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
    writeFileSync(
      join(directory, `${project}.fsproj`),
      `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference></PropertyGroup><ItemGroup><Reference Include="FSharp.Core"><HintPath>${hint}</HintPath></Reference>${reference}<Compile Include="Body.fs" /></ItemGroup></Project>`,
    );
    writeFileSync(join(directory, "Body.fs"), `module ${project}\nlet value = 1\n`);
  }
  const restore = spawnSync(
    executable("dotnet"),
    ["restore", "tests/selected/selected.fsproj", "--use-lock-file"],
    { cwd: root, encoding: "utf8" },
  );
  assert.equal(restore.status, 0, restore.stdout + restore.stderr);
}

test("native solution groups retain all prerequisites and exact configuration properties", () => {
  /** @type {Pick<import("./registry.mjs").Suite, "kind" | "platforms">} */
  const common = { kind: "dotnet", platforms: ["linux"] };
  /** @type {import("./registry.mjs").Suite[]} */
  const suites = [
    { ...common, id: "unit", project: "unit.fsproj", build: ["cli.fsproj"] },
    { ...common, id: "web", project: "web.fsproj", build: ["cli.fsproj"] },
    {
      ...common,
      id: "architecture",
      project: "architecture.fsproj",
      configuration: "Debug",
      msbuild: ["-p:Optimize=false"],
    },
    { ...common, id: "debug", project: "debug.fsproj", configuration: "Debug" },
  ];
  assert.deepEqual(buildGroups(suites), [
    {
      configuration: "Release",
      msbuild: [],
      projects: ["cli.fsproj", "unit.fsproj", "web.fsproj"],
    },
    { configuration: "Debug", msbuild: ["-p:Optimize=false"], projects: ["architecture.fsproj"] },
    { configuration: "Debug", msbuild: [], projects: ["debug.fsproj"] },
  ]);
});

test("generated solution paths preserve spaces and XML characters and refuse absent inputs", () => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-suite-solution-"));
  try {
    mkdirSync(join(root, "tests"));
    writeFileSync(join(root, "tests", 'A & "B".fsproj'), "<Project />");
    const directory = join(root, "artifacts", "suite-build", "fixture");
    assert.equal(
      solutionText(root, directory, ['tests/A & "B".fsproj']),
      '<Solution>\n  <Project Path="../../../tests/A &amp; &quot;B&quot;.fsproj" />\n</Solution>\n',
    );
    assert.throws(() => solutionText(root, directory, ["tests/missing.fsproj"]), /missing/u);
    assert.throws(() => solutionText(root, directory, ["../outside.fsproj"]), /unsafe/u);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test(
  "a partial native solution retains Release for an unlisted project dependency",
  { timeout: 60_000 },
  () => {
    const root = mkdtempSync(join(tmpdir(), "claimcore-sdk-configuration-"));
    let passed = false;
    try {
      configurationFixture(root);
      buildSuites(root, [
        {
          id: "selected",
          kind: "dotnet",
          platforms: ["linux"],
          project: "tests/selected/selected.fsproj",
          configuration: "Release",
        },
      ]);
      assert.ok(existsSync(join(root, "tests/dependency/bin/Release/net10.0/dependency.dll")));
      assert.equal(
        existsSync(join(root, "tests/dependency/bin/Debug/net10.0/dependency.dll")),
        false,
      );
      assert.ok(existsSync(join(root, "tests/selected/bin/Release/net10.0/selected.dll")));
      passed = true;
    } finally {
      if (passed) {
        rmSync(root, { recursive: true, force: true });
      } else {
        process.stderr.write(`Failed SDK configuration fixture retained: ${root}.\n`);
      }
    }
  },
);
