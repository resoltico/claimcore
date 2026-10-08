import test from "node:test";
import { must } from "./test-support.mjs";
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { validateWorkflowSources } from "./workflow-policy.mjs";
import { workflowSources } from "./workflow-sources.mjs";
import { parseWorkflow } from "./yaml.mjs";
import { mkdtempSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { spawnSync } from "node:child_process";

const root = fileURLToPath(new URL("../..", import.meta.url));
const pin = "1".repeat(40);
const compliant = `on: workflow_dispatch\npermissions: {contents: read}\njobs:\n  probe:\n    runs-on: ubuntu-24.04\n    steps:\n      - uses: actions/checkout@${pin} # v7.0.1\n        with:\n          persist-credentials: false\n`;
const isolated = (text = compliant, path = ".github/workflows/probe.yml") =>
  new Map([[path, text]]);
/** @param {string} text @param {string} [path] */
const verify = (text, path) => validateWorkflowSources(isolated(text, path), { graph: false });

for (const extension of ["yml", "yaml"]) {
  test(`parses real credential settings in .${extension} workflows`, () => {
    assert.equal(verify(compliant, `.github/workflows/probe.${extension}`).workflows, 1);
    assert.throws(
      () =>
        verify(
          compliant.replace(
            "persist-credentials: false",
            "persist-credentials: true\n          # persist-credentials: false",
          ),
          `.github/workflows/probe.${extension}`,
        ),
      /persist/u,
    );
  });
}
/** @type {Array<[string, (source: string) => string]>} */
const transforms = [
  ["missing checkout setting", (s) => s.replace("persist-credentials: false", "fetch-depth: 1")],
  [
    "comment-only checkout setting",
    (s) => s.replace("persist-credentials: false", "# persist-credentials: false"),
  ],
  ["tag action reference", (s) => s.replace(pin, "v7")],
  ["missing reviewed pin comment", (s) => s.replace(" # v7.0.1", "")],
  ["direct SDK selector", (s) => s.replace("actions/checkout", "actions/setup-dotnet")],
  ["write-default permissions", (s) => s.replace("contents: read", "contents: write")],
  ["privileged PR event", (s) => s.replace("workflow_dispatch", "pull_request_target")],
  ["duplicate mapping keys", (s) => `${s}permissions: {}\n`],
  [
    "alias configuration",
    (s) =>
      `copy: &copy false\n${s.replace("persist-credentials: false", "persist-credentials: *copy")}`,
  ],
  ["moving runner alias", (s) => s.replace("ubuntu-24.04", "ubuntu-latest")],
  [
    "moving matrix runner alias",
    (s) =>
      s.replace(
        "    runs-on: ubuntu-24.04\n",
        "    runs-on: ${{ matrix.os }}\n    strategy:\n      matrix:\n        include:\n          - os: macos-latest\n",
      ),
  ],
  ["fake aggregate name", (s) => s.replace("    runs-on:", "    name: Gate\n    runs-on:")],
];
for (const [label, transform] of transforms) {
  test(`refuses ${label}`, () => assert.throws(() => verify(transform(compliant))));
}

test("accepts quoted false and inspects action.yaml composite steps", () => {
  assert.equal(
    verify(compliant.replace("persist-credentials: false", 'persist-credentials: "false"'))
      .workflows,
    1,
  );
  const sources = isolated();
  sources.set(
    ".github/actions/toolchain/action.yaml",
    `runs:\n  using: composite\n  steps:\n    - uses: actions/setup-node@${pin} # v7.0.0\n`,
  );
  assert.equal(validateWorkflowSources(sources, { graph: false }).compositeActions, 1);
  sources.set(
    ".github/actions/toolchain/action.yaml",
    "runs:\n  using: composite\n  steps:\n    - uses: actions/setup-node@main\n",
  );
  assert.throws(() => validateWorkflowSources(sources, { graph: false }));
});

/** @param {string} path @param {(source: string) => string} modify */
function changed(path, modify) {
  const sources = workflowSources(root);
  const key = `.github/workflows/${path}`;
  assert(sources.has(key));
  const old = must(sources.get(key));
  const next = modify(old);
  assert.notEqual(old, next, "Control must actually alter its source.");
  sources.set(key, next);
  return sources;
}

test("validates the complete real workflow graph", () => {
  assert(validateWorkflowSources(workflowSources(root)).workflows >= 10);
});
test("acceptance retains sanitized browser failures before CLI results exist", () => {
  const source = must(workflowSources(root).get(".github/workflows/verify-acceptance.yml"));
  const { steps } = parseWorkflow(source).value.jobs.acceptance;
  const packaging = must(
    steps.find(
      (/** @type {import("./types.mjs").Json} */ step) =>
        step.name === "Retain sanitized browser reports with acceptance evidence",
    ),
  );
  assert.equal(packaging.if, "always()");
  assert.equal(packaging.shell, "bash");
  const upload = must(
    steps.find((/** @type {import("./types.mjs").Json} */ step) =>
      step.uses?.startsWith("actions/upload-artifact@"),
    ),
  );
  assert.equal(upload.with.path, "artifacts/test-results/acceptance/");
  for (const diagnostics of [false, true]) {
    const scratch = mkdtempSync(join(tmpdir(), "claimcore-acceptance-evidence-"));
    try {
      mkdirSync(join(scratch, "artifacts/browser"), { recursive: true });
      mkdirSync(join(scratch, "artifacts/browser-failures"));
      writeFileSync(join(scratch, "artifacts/browser-failures/private.log"), "PRIVATE-SENTINEL");
      writeFileSync(join(scratch, "artifacts/browser/unreviewed.json"), "UNREVIEWED-SENTINEL");
      const files = ["chromium.json", ...(diagnostics ? ["chromium.diagnostics.json"] : [])];
      for (const file of files) {
        writeFileSync(join(scratch, "artifacts/browser", file), `{"source":"${file}"}\n`);
      }
      assert.equal(spawnSync("bash", ["-c", packaging.run], { cwd: scratch }).status, 0);
      const destination = join(scratch, "artifacts/test-results/acceptance/browser");
      assert.deepEqual(readdirSync(destination).sort(), files.sort());
      for (const file of files) {
        assert.deepEqual(
          readFileSync(join(destination, file)),
          readFileSync(join(scratch, "artifacts/browser", file)),
        );
      }
    } finally {
      rmSync(scratch, { recursive: true, force: true });
    }
  }
});
/** @type {Array<[string, string, (source: string) => string]>} */
const graphControls = [
  [
    "mandatory job outside Gate",
    "ci.yml",
    (s) => `${s}\n  forgotten:\n    uses: ./.github/workflows/verify-suites.yml\n`,
  ],
  ["failure-permissive Gate", "ci.yml", (s) => s.replace('test "$result" = "success"', "true")],
  [
    "shared manual and push concurrency",
    "ci.yml",
    (s) => s.replace("-${{ github.event_name }}", ""),
  ],
  [
    "upload outside the scanned path",
    "verify-frontend.yml",
    (s) => s.replace("path: artifacts/frontend/", "path: artifacts/elsewhere/"),
  ],
  [
    "upload without a successful scan",
    "verify-suites.yml",
    (s) => s.replace("always() && steps.artifact_scan.outcome == 'success'", "always()"),
  ],
  [
    "scan by the retired PowerShell scanner",
    "verify-coverage.yml",
    (s) =>
      s.replace("node eng/ci/scan/main.mjs artifacts", "pwsh -File eng/Scan-ArtifactSecrets.ps1"),
  ],
  ["unprotected publisher", "release.yml", (s) => s.replace("    environment: release\n", "")],
];
for (const [name, path, modify] of graphControls) {
  test(`real graph refuses ${name}`, () =>
    assert.throws(() => validateWorkflowSources(changed(path, modify))));
}

test("an orphan .yaml verifier cannot evade graph reachability", () => {
  const sources = workflowSources(root);
  sources.set(
    ".github/workflows/verify-orphan.yaml",
    compliant.replace("on: workflow_dispatch", "on: workflow_call"),
  );
  assert.throws(() => validateWorkflowSources(sources), /disconnected/u);
});

/** @type {Array<[string, string, string, string]>} */
const executionControls = [
  [
    "skipped Gate step",
    "ci.yml",
    "- name: Require every verification family",
    "- name: Require every verification family\n        if: false",
  ],
  [
    "failure-permissive Gate step",
    "ci.yml",
    "- name: Require every verification family",
    "- name: Require every verification family\n        continue-on-error: true",
  ],
  [
    "commented artifact scan result",
    "verify-frontend.yml",
    "always() && steps.artifact_scan.outcome == 'success'",
    "always() # steps.artifact_scan.outcome == 'success'",
  ],
  [
    "widened publication condition",
    "release.yml",
    "github.ref == 'refs/heads/main'",
    "github.ref == 'refs/heads/main' || true",
  ],
];
for (const [label, path, before, after] of executionControls) {
  test(`rejects ${label} in parsed execution settings`, () => {
    const values = workflowSources(root);
    const name = `.github/workflows/${path}`;
    const current = must(values.get(name));
    assert(current.includes(before));
    values.set(name, current.replaceAll(before, after));
    assert.throws(() => validateWorkflowSources(values));
  });
}
