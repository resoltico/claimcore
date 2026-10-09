import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { must } from "./test-support.mjs";
import { fileURLToPath } from "node:url";
import { affected } from "./local-scope.mjs";
import { validatePlan } from "./stage-plan.mjs";
import { parseWorkflow } from "./yaml.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
/** @type {import("./run-local.mjs").LocalRegistry} */
const registry = JSON.parse(readFileSync(join(root, "eng/ci/local-plan.json"), "utf8"));
/** @param {string} id */
const job = (id) => must(registry.jobs.find((candidate) => candidate.id === id));

test("every CI verification family is mirrored locally or explained", () => {
  const ci = parseWorkflow(readFileSync(join(root, ".github/workflows/ci.yml"), "utf8")).value;
  const families = Object.keys(ci.jobs).filter((name) => name !== "gate");
  const covered = new Set([
    ...registry.jobs.flatMap((entry) => entry.mirrors),
    ...registry.notLocal.map((entry) => entry.family),
  ]);
  assert.deepEqual(
    families.filter((family) => !covered.has(family)),
    [],
    "add each new CI family to eng/ci/local-plan.json as a job or as notLocal with a reason",
  );
  const known = new Set(families);
  const stale = [...covered].filter((family) => !known.has(family));
  assert.deepEqual(stale, [], "the local plan names a CI family that no longer exists");
  for (const entry of registry.notLocal) {
    assert.ok(entry.reason.length > 20, `${entry.family} needs a reason`);
  }
});

test("the local jobs form a valid plan whose commands exist", () => {
  validatePlan({
    producer: "local",
    stages: registry.jobs.map((entry) => ({
      id: entry.id,
      argv: entry.argv,
      after: entry.after,
    })),
  });
  for (const entry of registry.jobs) {
    for (const pattern of entry.scope ?? []) {
      assert.doesNotThrow(() => new RegExp(pattern, "u"));
    }
    for (const part of entry.argv.filter((value) => /^eng\/.*\.(mjs|sh)$/u.test(value))) {
      assert.ok(existsSync(join(root, part)), `${entry.id} runs ${part}, which does not exist`);
    }
    assert.ok(entry.mirrors.length > 0, `${entry.id} must name the CI family it mirrors`);
  }
});

test("a documentation-only change runs neither the frontend nor the database suites", () => {
  const changed = ["docs/development.md", "CHANGELOG.md"];
  assert.equal(affected(job("frontend-gates"), changed), false);
  assert.equal(affected(job("tests-postgres"), changed), false);
  assert.equal(affected(job("quality"), changed), true, "repository gates always run");
  assert.equal(
    affected(job("tests-dotnet"), changed),
    true,
    "documentation tests read the documents",
  );
});

test("changes select the jobs that can be affected by them", () => {
  assert.equal(affected(job("frontend-gates"), ["web/src/App.tsx"]), true);
  assert.equal(affected(job("tests-postgres"), ["web/src/App.tsx"]), false);
  assert.equal(
    affected(job("tests-postgres"), ["src/ClaimCore.Postgres/RuntimeDatabase.fs"]),
    true,
  );
  assert.equal(affected(job("tests-postgres"), ["db/baseline/case-state-and-lineage.sql"]), true);
  assert.equal(affected(job("tests-postgres"), ["eng/backup/capture_delivery.py"]), true);
  assert.equal(affected(job("tests-postgres"), ["eng/backup/Test-ManagedBackup.sh"]), true);
  assert.equal(affected(job("tests-postgres"), ["eng/backup/README.md"]), false);
  assert.equal(affected(job("frontend-gates"), ["src/ClaimCore.Contracts/Endpoints.fs"]), true);
  assert.equal(affected(job("tests-dotnet"), ["eng/ci/suites/suite.mjs"]), true);
  assert.equal(affected(job("browser-coverage"), ["eng/Generate-SyntheticWebTls.sh"]), true);
  assert.equal(affected(job("browser-coverage"), ["eng/oidc/Run-SyntheticOidc.sh"]), true);
});

test("when the change set is unknown every job runs", () => {
  for (const entry of registry.jobs) {
    assert.equal(affected(entry, null), true);
  }
});

test("local runs have no shared cleanup registry", () => {
  assert.equal(Reflect.has(registry, "clean"), false);
});

test("shared lint policy changes require the frontend gates", () => {
  assert.equal(affected(job("frontend-gates"), ["config/oxlint.json"]), true);
});

test("deployment selection covers publication, trust-driver, lock and shared toolchain inputs", () => {
  const deployment = job("container-operation");
  const inputs = [
    "web/e2e/browser-trust-driver.mjs",
    "web/e2e/browser-trust-navigation.mjs",
    "web/e2e/browser-trust-store.mjs",
    "web/package.json",
    "web/package-lock.json",
    "web/.npmrc",
    "web/src/App.tsx",
    "web/scripts/write-asset-manifest.mjs",
    ".node-version",
    "db/baseline/case-state-and-lineage.sql",
    "db/postgresql-baseline.json",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "NuGet.Config",
    ".config/dotnet-tools.json",
    "eng/ci/publish/main.mjs",
    "eng/ClaimCore.ContractGenerator/DefaultPresentation.fs",
    "eng/package-lock.json",
    "config/contracts.lock.json",
    "src/ClaimCore.Witness/PostgresTransport.fs",
    "deployment/Dockerfile",
    "ClaimCore.slnx",
    "global.json",
    "LICENSE",
    "prettier.config.mjs",
    ".editorconfig",
    ".github/actions/toolchain/action.yml",
    ".github/workflows/verify-deployment.yml",
  ];
  for (const input of inputs) {
    assert.equal(affected(deployment, [input]), true, input);
  }
  for (const input of ["docs/web.md", "CHANGELOG.md", "SUPPORT.md", "examples/README.md"]) {
    assert.equal(affected(deployment, [input]), false, input);
  }
  const incomplete = { ...deployment, scope: ["^deployment/", "^src/", "^config/"] };
  for (const input of ["web/e2e/browser-trust-driver.mjs", "web/package-lock.json"]) {
    assert.equal(affected(incomplete, [input]), false, "old scope misses this independent input");
  }
});

test("one combined published job owns every client and coverage family", () => {
  const combined = job("browser-coverage");
  assert.deepEqual(combined.mirrors, ["publish", "acceptance", "browser", "coverage"]);
  assert.deepEqual(combined.argv, ["bash", "eng/Run-LocalBrowserCoverage.sh"]);
  assert.equal(
    registry.jobs.some((entry) => entry.id === "published-cli"),
    false,
  );
  for (const family of combined.mirrors) {
    assert.equal(registry.jobs.filter((entry) => entry.mirrors.includes(family)).length, 1);
  }
});
