import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { checkRegistry } from "./check-registry.mjs";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");

const registry = {
  schemaVersion: 2,
  suites: [
    {
      id: "unit",
      kind: "dotnet",
      assembly: "Example.Tests",
      project: "tests/Example.Tests/Example.Tests.fsproj",
      configuration: "Release",
      coverage: true,
      platforms: ["linux"],
      timeout: "25m",
    },
  ],
};

/**
 * @param {Record<string, string>} extra Files to add or replace.
 * @returns {string[]}
 */
function problems(extra) {
  const root = mkdtempSync(join(tmpdir(), "claimcore-registry-"));
  try {
    const files = {
      "config/test-suites.json": JSON.stringify(registry),
      "tests/Example.Tests/Example.Tests.fsproj":
        "<Project><PropertyGroup><IsTestProject>true</IsTestProject><OutputType>Exe</OutputType><AssemblyName>Example.Tests</AssemblyName></PropertyGroup></Project>",
      "tests/inventory/Example.Tests.txt": "a test\n",
      ".github/workflows/ci.yml": "name: ci\n",
      ...extra,
    };
    for (const [path, content] of Object.entries(files)) {
      mkdirSync(dirname(join(root, path)), { recursive: true });
      writeFileSync(join(root, path), content);
    }
    return checkRegistry(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("the repository agrees with its suite registry", () => {
  assert.deepEqual(checkRegistry(repository), []);
});

test("a consistent fixture has no problems", () => {
  assert.deepEqual(problems({}), []);
});

test("an unregistered test project, orphan inventory or repeated count is reported", () => {
  assert.match(
    String(
      problems({
        "tests/Other.Tests/Other.Tests.fsproj":
          "<Project><PropertyGroup><IsTestProject>true</IsTestProject><OutputType>Exe</OutputType><AssemblyName>Example.Tests</AssemblyName></PropertyGroup></Project>",
      }),
    ),
    /no suite registers/u,
  );
  assert.match(
    String(problems({ "tests/inventory/Orphan.txt": "x\n" })),
    /belongs to no registered suite/u,
  );
  assert.match(
    String(
      problems({ ".github/workflows/ci.yml": "run: dotnet test --minimum-expected-tests=12\n" }),
    ),
    /repeats a test count/u,
  );
});

test("a registered file that does not exist is reported", () => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-registry-missing-"));
  try {
    mkdirSync(join(root, "config"), { recursive: true });
    writeFileSync(join(root, "config/test-suites.json"), JSON.stringify(registry));
    const found = checkRegistry(root);
    assert.ok(found.some((error) => /project.*does not exist|does not exist/u.test(error)));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
