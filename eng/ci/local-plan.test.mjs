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
  assert.equal(affected(job("published-cli"), changed), false);
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
  assert.equal(affected(job("tests-postgres"), ["db/baseline.sql"]), true);
  assert.equal(affected(job("frontend-gates"), ["src/ClaimCore.Contracts/Endpoints.fs"]), true);
  assert.equal(affected(job("tests-dotnet"), ["eng/ci/suites/suite.mjs"]), true);
});

test("when the change set is unknown every job runs", () => {
  for (const entry of registry.jobs) {
    assert.equal(affected(entry, null), true);
  }
});

test("only generated stage outputs are cleaned before a local run", () => {
  for (const path of registry.clean ?? []) {
    assert.match(
      path,
      /^artifacts\/[a-z-]+$/u,
      "clean paths are single directories under artifacts/",
    );
  }
});

test("shared lint policy changes require the frontend gates", () => {
  assert.equal(affected(job("frontend-gates"), ["config/oxlint.json"]), true);
});
