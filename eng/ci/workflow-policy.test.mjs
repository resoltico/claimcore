import test from "node:test";
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { validateWorkflowSources } from "./workflow-policy.mjs";
import { workflowSources } from "./workflow-sources.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const pin = "1".repeat(40);
const compliant = `on: workflow_dispatch\npermissions: {contents: read}\njobs:\n  probe:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@${pin} # v7.0.1\n        with:\n          persist-credentials: false\n`;
const isolated = (text = compliant, path = ".github/workflows/probe.yml") =>
  new Map([[path, text]]);
const verify = (text, path) =>
  validateWorkflowSources(isolated(text, path), { graph: false });

for (const extension of ["yml", "yaml"]) {
  test(`parses real credential settings in .${extension} workflows`, () => {
    assert.equal(
      verify(compliant, `.github/workflows/probe.${extension}`).workflows,
      1,
    );
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
for (const [label, transform] of [
  [
    "missing checkout setting",
    (s) => s.replace("persist-credentials: false", "fetch-depth: 1"),
  ],
  [
    "comment-only checkout setting",
    (s) =>
      s.replace("persist-credentials: false", "# persist-credentials: false"),
  ],
  ["tag action reference", (s) => s.replace(pin, "v7")],
  ["missing reviewed pin comment", (s) => s.replace(" # v7.0.1", "")],
  [
    "direct SDK selector",
    (s) => s.replace("actions/checkout", "actions/setup-dotnet"),
  ],
  [
    "write-default permissions",
    (s) => s.replace("contents: read", "contents: write"),
  ],
  [
    "privileged PR event",
    (s) => s.replace("workflow_dispatch", "pull_request_target"),
  ],
  ["duplicate mapping keys", (s) => s + "permissions: {}\n"],
  [
    "alias configuration",
    (s) =>
      "copy: &copy false\n" +
      s.replace("persist-credentials: false", "persist-credentials: *copy"),
  ],
  [
    "fake aggregate name",
    (s) => s.replace("    runs-on:", "    name: Gate\n    runs-on:"),
  ],
]) {
  test(`refuses ${label}`, () =>
    assert.throws(() => verify(transform(compliant))));
}

test("accepts quoted false and inspects action.yaml composite steps", () => {
  assert.equal(
    verify(
      compliant.replace(
        "persist-credentials: false",
        'persist-credentials: "false"',
      ),
    ).workflows,
    1,
  );
  const sources = isolated();
  sources.set(
    ".github/actions/toolchain/action.yaml",
    `runs:\n  using: composite\n  steps:\n    - uses: actions/setup-node@${pin} # v7.0.0\n`,
  );
  assert.equal(
    validateWorkflowSources(sources, { graph: false }).compositeActions,
    1,
  );
  sources.set(
    ".github/actions/toolchain/action.yaml",
    "runs:\n  using: composite\n  steps:\n    - uses: actions/setup-node@main\n",
  );
  assert.throws(() => validateWorkflowSources(sources, { graph: false }));
});

function changed(path, modify) {
  const sources = workflowSources(root);
  const key = `.github/workflows/${path}`;
  assert(sources.has(key));
  const old = sources.get(key);
  const next = modify(old);
  assert.notEqual(old, next, "Control must actually alter its source.");
  sources.set(key, next);
  return sources;
}

test("validates the complete real workflow graph", () => {
  assert(validateWorkflowSources(workflowSources(root)).workflows >= 15);
});
for (const [name, path, modify] of [
  [
    "mandatory job outside Gate",
    "ci.yml",
    (s) =>
      s + "\n  forgotten:\n    uses: ./.github/workflows/verify-unit.yml\n",
  ],
  [
    "failure-permissive Gate",
    "ci.yml",
    (s) => s.replace('test "$result" = "success"', "true"),
  ],
  [
    "shared manual and push concurrency",
    "ci.yml",
    (s) => s.replace("-${{ github.event_name }}", ""),
  ],
  [
    "missing producer isolation",
    "verify-evidence.yml",
    (s) => s.replace("merge-multiple: false", "merge-multiple: true"),
  ],
  [
    "duplicate security execution",
    "verify-frontend.yml",
    (s) =>
      s.replace(
        "          run_stage restore-frontend",
        "          run_stage dependency-security duplicate pwsh -File eng/Check-DependencySecurity.ps1\n          run_stage restore-frontend",
      ),
  ],
  [
    "currency reintroduced into PR gate",
    "verify-frontend.yml",
    (s) =>
      s.replace(
        "          run_stage restore-frontend",
        "          run_stage currency duplicate pwsh -File eng/Check-DependencyCurrency.ps1\n          run_stage restore-frontend",
      ),
  ],
  [
    "unprotected publisher",
    "publish-postgres-image.yml",
    (s) => s.replace("    environment: release\n", ""),
  ],
]) {
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

for (const [label, path, before, after] of [
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
])
  test(`rejects ${label} in parsed execution settings`, () => {
    const values = workflowSources(root);
    const name = `.github/workflows/${path}`;
    assert(values.get(name).includes(before));
    values.set(name, values.get(name).replaceAll(before, after));
    assert.throws(() => validateWorkflowSources(values));
  });
