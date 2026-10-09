// Stage outcomes select required evidence; every delivered file must still pass its owner oracle.
import assert from "node:assert/strict";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { option } from "./process-support.mjs";
import { parseSelection, selectSuites } from "./suites/selection.mjs";
import { loadSuites } from "./suites/registry.mjs";
import { artifactDirectory } from "./artifact-path.mjs";

/** @param {string} path */
const requireFile = (path) =>
  assert.ok(existsSync(path), "A passed producer is missing required evidence.");
/** @param {import("./run-context.mjs").RunContext} context @param {string[]} args */
function requireSuites(context, args) {
  const selection = parseSelection(args);
  const names = /** @type {Record<string,string>} */ ({ darwin: "macos", win32: "windows" });
  const platform = names[process.platform] ?? "linux";
  const suites = selectSuites(
    loadSuites(context.source),
    selection,
    selection.options["platform"] ?? platform,
  );
  const results = artifactDirectory(
    context.source,
    selection.options["results-root"] ?? "artifacts/test-results",
  );
  for (const suite of suites) {
    const assembly = suite.assembly ?? suite.id;
    if (suite.partitions === undefined) {
      requireFile(join(results, suite.id, `${assembly}.trx`));
    } else {
      for (const partition of suite.partitions.ids) {
        requireFile(join(results, suite.id, partition, `${assembly}.${partition}.trx`));
      }
    }
  }
}
/** @param {string} artifacts @param {string} kind */
function requireWrapper(artifacts, kind) {
  const workspaces = readdirSync(artifacts).filter((name) => name.startsWith(`${kind}.`));
  assert.equal(workspaces.length, 1, "A passed wrapper must provide its exact current run.");
  const workspace = join(artifacts, workspaces[0] ?? "");
  for (const product of ["cli", "web", "database"]) {
    requireFile(join(workspace, "manifests", `${product}.json`));
  }
  if (kind === "acceptance-local") {
    requireFile(join(workspace, "results/ClaimCore.AcceptanceTests.trx"));
    requireFile(join(workspace, "results/cli.coverage.cobertura.acceptance.xml"));
  }
}
/** @param {string} artifacts */
function requirePublishedCoverage(artifacts) {
  requireWrapper(artifacts, "local-browser");
  requireFile(join(artifacts, "coverage/merged/Cobertura.xml"));
  requireFile(join(artifacts, "coverage/input/acceptance/ClaimCore.AcceptanceTests.trx"));
  requireFile(join(artifacts, "coverage/input/acceptance/cli.coverage.cobertura.acceptance.xml"));
  for (const engine of ["chromium", "firefox", "webkit"]) {
    requireFile(join(artifacts, `browser/${engine}.json`));
    requireFile(join(artifacts, `coverage/input/browser/${engine}.coverage.cobertura.e2e.xml`));
  }
}
/** @param {import("./run-context.mjs").RunContext} context @param {string[]} args */
function requireStagePlan(context, args) {
  const [name] = args;
  assert.ok(name && /^[a-z0-9-]+$/u.test(name));
  const plan = /** @type {{stages:{id:string}[]}} */ (
    JSON.parse(readFileSync(join(context.source, `eng/ci/stage-plans/${name}.json`), "utf8"))
  );
  const only = option(args, "only", "").split(",").filter(Boolean);
  const selected = plan.stages
    .filter((stage) => only.length === 0 || only.includes(stage.id))
    .map((stage) => stage.id);
  if (selected.includes("frontend-unit") || selected.includes("frontend-report")) {
    requireFile(join(context.source, "artifacts/frontend/vitest-summary.json"));
  }
  if (selected.includes("frontend-mutation")) {
    requireFile(join(context.source, "web/artifacts/stryker/domain-mutation.json"));
  }
}
/** @param {import("./run-context.mjs").RunContext} context @param {string[]} args */
function requireBrowser(context, args) {
  const scope = args[2] ?? "all";
  const engines = scope === "all" ? ["chromium", "firefox", "webkit"] : [scope];
  assert.ok(engines.every((engine) => ["chromium", "firefox", "webkit"].includes(engine)));
  for (const engine of engines) {
    requireFile(join(context.source, `artifacts/browser/${engine}.json`));
  }
}
/** @param {import("./run-context.mjs").RunContext} context @param {string} artifacts */
function requireStandalone(context, artifacts) {
  const suite = context.requested.findIndex((arg) => arg.endsWith("eng/ci/suites/suite.mjs"));
  if (suite >= 0) {
    requireSuites(context, context.requested.slice(suite + 1));
  }
  const plan = context.requested.findIndex((arg) => arg.endsWith("eng/ci/run-stages.mjs"));
  if (plan >= 0) {
    requireStagePlan(context, context.requested.slice(plan + 1));
  }
  const browser = context.requested.findIndex((arg) => arg.endsWith("Run-PublishedWebE2E.sh"));
  if (browser >= 0) {
    requireBrowser(context, context.requested.slice(browser + 1));
  }
  if (context.requested.some((arg) => arg.endsWith("Run-PublishedCliAcceptance.sh"))) {
    requireWrapper(artifacts, "acceptance-local");
  }
  if (context.requested.some((arg) => arg.endsWith("Run-LocalBrowserCoverage.sh"))) {
    requirePublishedCoverage(artifacts);
  }
  if (context.requested.some((arg) => arg.endsWith("eng/ci/deployment/qualify.mjs"))) {
    requirePassedEvidence(context, { "container-operation": "passed" });
  }
}
/** @param {import("./run-context.mjs").RunContext} context @param {Record<string,string>} stages */
export function requirePassedEvidence(context, stages) {
  const passed = (/** @type {string} */ id) => stages[id]?.startsWith("passed") === true;
  const artifacts = join(context.source, "artifacts");
  if (passed("tests-dotnet")) {
    requireSuites(context, ["run", "--cross-platform"]);
  }
  if (passed("tests-postgres")) {
    requireSuites(context, ["run", "--group", "postgres"]);
  }
  if (passed("browser-coverage")) {
    requirePublishedCoverage(artifacts);
  }
  if (passed("frontend-gates")) {
    requireFile(join(artifacts, "frontend/prerequisites.json"));
    requireFile(join(artifacts, "frontend/vitest-summary.json"));
    requireFile(join(context.source, "web/artifacts/stryker/domain-mutation.json"));
  }
  if (passed("container-operation")) {
    const runs = readdirSync(artifacts).filter((name) =>
      /^claimcore-operating-[0-9a-f]{16}$/u.test(name),
    );
    assert.equal(runs.length, 1);
    requireFile(join(artifacts, runs[0] ?? "", "result.json"));
  }
  if (stages["command"] === "exit 0") {
    requireStandalone(context, artifacts);
  }
}
