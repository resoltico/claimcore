import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
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

/** @param {string} script @param {Partial<import("node:child_process").SpawnSyncOptionsWithStringEncoding>} [options] */
function nativeFailure(script, options = {}) {
  return projectMembershipProblems(
    ".",
    ["synthetic.fsproj"],
    [],
    (root, project, configuration, properties) =>
      evaluateTestProject(root, project, configuration, properties, (_command, _args, admitted) => {
        assert.equal(admitted.timeout, 30_000);
        assert.equal(admitted.maxBuffer, 1024 * 1024);
        return spawnSync(process.execPath, ["-e", script], { ...admitted, ...options });
      }),
  );
}

test("native classification failure retains closed exit and property refusal facts without captured payload", () => {
  const marker = "PRIVATE-CLASSIFICATION-SENTINEL";
  /** @type {[string,string,string][]} */
  const cases = [
    [`process.stderr.write(${JSON.stringify(marker)});process.exit(9)`, "exit", "9"],
    [`process.stdout.write(${JSON.stringify(marker)})`, "property-json", "0"],
    [
      'process.stdout.write(JSON.stringify({Properties:{IsTestProject:"true"}}))',
      "property-incomplete",
      "0",
    ],
    [
      'process.stdout.write(JSON.stringify({Properties:{IsTestProject:"invalid",OutputType:"Exe",AssemblyName:"Synthetic"}}))',
      "property-invalid",
      "0",
    ],
  ];
  for (const [script, reason, exit] of cases) {
    const errors = nativeFailure(script);
    assert.equal(errors.length, 2);
    for (const error of errors) {
      assert.ok(error.includes(`reason=${reason}; exit=${exit};`));
      assert.match(error, /elapsedMs=\d+/u);
      assert.ok(!error.includes(marker));
    }
  }
});

test("native classification distinguishes deadline, capture overflow and start refusal without changing admission budgets", () => {
  /** @type {[string,Partial<import("node:child_process").SpawnSyncOptionsWithStringEncoding>,string,string][]} */
  const cases = [
    ["setInterval(()=>{},1000)", { timeout: 50 }, "deadline", "ETIMEDOUT"],
    ['process.stdout.write("x".repeat(2*1024*1024))', {}, "capture-limit", "ENOBUFS"],
  ];
  for (const [script, options, reason, code] of cases) {
    const errors = nativeFailure(script, options);
    assert.equal(errors.length, 2);
    for (const error of errors) {
      assert.ok(error.includes(`reason=${reason};`));
      assert.ok(error.includes(`errorCode=${code};`));
    }
  }
  const errors = projectMembershipProblems(
    ".",
    ["synthetic.fsproj"],
    [],
    (root, project, configuration, properties) =>
      evaluateTestProject(root, project, configuration, properties, (_command, _args, options) =>
        spawnSync(join(tmpdir(), `claimcore-missing-command-${process.pid}`), [], options),
      ),
  );
  assert.equal(errors.length, 2);
  assert.ok(
    errors.every((error) => error.includes("reason=start;") && error.includes("errorCode=ENOENT;")),
  );
});

test("unknown classification exceptions expose only the fixed refusal category", () => {
  const errors = projectMembershipProblems(".", ["synthetic.fsproj"], [], () => {
    throw new Error("PRIVATE-UNKNOWN-SENTINEL");
  });
  assert.equal(errors.length, 2);
  assert.ok(errors.every((error) => error.endsWith("reason=unknown.")));
  assert.ok(errors.every((error) => !error.includes("PRIVATE-UNKNOWN-SENTINEL")));
});

test("unrecognized native failure exposes closed process uncertainty without provider code or message", () => {
  const canary = "PRIVATE-NATIVE-ERROR-SENTINEL";
  const errors = projectMembershipProblems(
    ".",
    ["synthetic.fsproj"],
    [],
    (root, project, configuration, properties) =>
      evaluateTestProject(root, project, configuration, properties, (_command, _args, options) => {
        const result = spawnSync(process.execPath, ["-e", "process.exit(0)"], options);
        result.error = Object.assign(new Error(canary), { code: canary });
        return result;
      }),
  );
  assert.equal(errors.length, 2);
  assert.ok(errors.every((error) => error.includes("reason=process-error; exit=0;")));
  assert.ok(errors.every((error) => error.includes("errorCode=unknown;")));
  assert.ok(errors.every((error) => !error.includes(canary)));
});
