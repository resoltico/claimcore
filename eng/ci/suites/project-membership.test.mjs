import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { evaluateTestProject, projectMembershipProblems } from "./project-membership.mjs";

/** @type {import("./registry.mjs").Suite[]} */
const registry = [
  {
    id: "registered",
    kind: "dotnet",
    project: "nested/arbitrary.fsproj",
    assembly: "Actual.Tests",
    configuration: "Release",
    platforms: ["linux", "macos", "windows"],
  },
];
/** @param {(root:string,project:string)=>void} body */
function fixture(body) {
  const root = mkdtempSync(join(tmpdir(), "claimcore-test-metadata-"));
  const project = "nested/arbitrary.fsproj";
  mkdirSync(dirname(join(root, project)), { recursive: true });
  writeFileSync(
    join(root, "global.json"),
    readFileSync(resolve(import.meta.dirname, "../../../global.json")),
  );
  writeFileSync(join(root, project), '<Project><Import Project="metadata.props" /></Project>');
  try {
    body(root, project);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}
/** @param {string} root @param {string} properties */
const metadata = (root, properties) =>
  writeFileSync(
    join(root, "nested/metadata.props"),
    `<Project><PropertyGroup>${properties}</PropertyGroup></Project>`,
  );

test("native imported metadata discovers an unconventionally named nested executable test", () => {
  fixture((root, project) => {
    const [registered] = registry;
    assert.ok(registered);
    metadata(
      root,
      "<IsTestProject>true</IsTestProject><OutputType>Exe</OutputType><AssemblyName>Actual.Tests</AssemblyName>",
    );
    assert.deepEqual(evaluateTestProject(root, project, "Release", []), {
      isTest: true,
      outputType: "exe",
      assembly: "Actual.Tests",
    });
    assert.match(String(projectMembershipProblems(root, [project], [])), /no suite registers/u);
    assert.deepEqual(projectMembershipProblems(root, [project], registry), []);
    assert.match(
      String(
        projectMembershipProblems(
          root,
          [project],
          [...registry, { ...registered, id: "duplicate" }],
        ),
      ),
      /multiple suites/u,
    );
  });
});

test("native conditional classification, test libraries and non-test registration cannot escape reconciliation", () => {
  fixture((root, project) => {
    metadata(
      root,
      "<IsTestProject Condition=\"'$(Configuration)' == 'Debug'\">true</IsTestProject><OutputType>Exe</OutputType><AssemblyName>Actual.Tests</AssemblyName>",
    );
    assert.match(String(projectMembershipProblems(root, [project], registry)), /inconsistent/u);
    metadata(
      root,
      "<IsTestProject>true</IsTestProject><OutputType>Library</OutputType><AssemblyName>Actual.Tests</AssemblyName>",
    );
    assert.match(String(projectMembershipProblems(root, [project], registry)), /not executable/u);
    metadata(
      root,
      "<IsTestProject>false</IsTestProject><OutputType>Library</OutputType><AssemblyName>Fixture</AssemblyName>",
    );
    assert.deepEqual(projectMembershipProblems(root, [project], []), []);
    assert.match(String(projectMembershipProblems(root, [project], registry)), /inconsistent/u);
  });
});
