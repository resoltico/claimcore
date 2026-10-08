import assert from "node:assert/strict";
import test from "node:test";
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
  existsSync,
  symlinkSync,
  unlinkSync,
  readdirSync,
} from "node:fs";
import { spawnSync } from "node:child_process";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { checkUploads } from "./workflow-uploads.mjs";
import { parseWorkflow } from "./yaml.mjs";

const guard = "always() && steps.artifact_scan.outcome == 'success'";
const sourceReport = "web/artifacts/stryker/domain-mutation.json";
const retainedReport = "artifacts/frontend/domain-mutation.json";

function mutationRetentionCommand() {
  const workflow = parseWorkflow(
    readFileSync(new URL("../../.github/workflows/verify-frontend.yml", import.meta.url), "utf8"),
  ).value;
  /** @type {import("./types.mjs").Json[]} */
  const actual = workflow.jobs.frontend.steps;
  const retain = actual.find((step) => step.id === "mutation_evidence");
  assert.ok(retain);
  assert.equal(retain.if, "always()");
  assert.ok(actual.indexOf(retain) < actual.findIndex((step) => step.id === "artifact_scan"));
  checkUploads(actual);
  const upload = actual.find((step) => step.uses?.startsWith("actions/upload-artifact@"));
  assert.ok(upload);
  assert.equal(
    upload.if,
    `\${{ ${guard} && steps.job_checkout_current.outcome == 'success' && steps.mutation_evidence.outcome == 'success' }}`,
  );
  return retain.run;
}

for (const produced of [true, false]) {
  test(`frontend evidence preserves diagnostics with mutation report ${produced ? "present" : "absent"}`, () => {
    const command = mutationRetentionCommand();
    const root = mkdtempSync(join(tmpdir(), "claimcore-mutation-evidence-"));
    const mutation = '{"files":{"synthetic":{"mutants":[{"status":"Killed"}]}}}\n';
    try {
      mkdirSync(join(root, "artifacts/frontend"), { recursive: true });
      writeFileSync(join(root, "artifacts/frontend/vitest-summary.json"), "diagnostic\n");
      if (produced) {
        mkdirSync(join(root, "web/artifacts/stryker"), { recursive: true });
        writeFileSync(join(root, sourceReport), mutation);
      }
      assert.equal(spawnSync("bash", ["-euo", "pipefail", "-c", command], { cwd: root }).status, 0);
      const retained = join(root, retainedReport);
      assert.equal(existsSync(retained), produced);
      if (produced) {
        assert.equal(readFileSync(retained, "utf8"), mutation);
      }
      assert.equal(
        readFileSync(join(root, "artifacts/frontend/vitest-summary.json"), "utf8"),
        "diagnostic\n",
      );
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
}

/** @type {Record<string, (root: string) => void>} */
const refusedMutationReports = {
  "linked source": (root) => {
    unlinkSync(join(root, sourceReport));
    symlinkSync(join(root, "private/keep.json"), join(root, sourceReport));
  },
  "linked source ancestor": (root) => {
    rmSync(join(root, "web/artifacts/stryker"), { recursive: true });
    writeFileSync(join(root, "private/domain-mutation.json"), "private source\n");
    symlinkSync(join(root, "private"), join(root, "web/artifacts/stryker"));
  },
  "linked destination": (root) => {
    symlinkSync(join(root, "private/keep.json"), join(root, retainedReport));
  },
  "linked destination ancestor": (root) => {
    rmSync(join(root, "artifacts/frontend"), { recursive: true });
    symlinkSync(join(root, "private"), join(root, "artifacts/frontend"));
  },
  "pre-existing destination": (root) => {
    writeFileSync(join(root, retainedReport), "prior report\n");
  },
  "non-regular source": (root) => {
    unlinkSync(join(root, sourceReport));
    mkdirSync(join(root, sourceReport));
  },
};

/** @param {string} root */
const privateFixtureState = (root) =>
  readdirSync(join(root, "private"))
    .sort()
    .map((name) => [name, readFileSync(join(root, "private", name), "utf8")]);

for (const [kind, prepare] of Object.entries(refusedMutationReports)) {
  test(`frontend mutation retention refuses ${kind} without copying or overwriting private bytes`, () => {
    const root = mkdtempSync(join(tmpdir(), "claimcore-mutation-refusal-"));
    try {
      for (const directory of ["web/artifacts/stryker", "artifacts/frontend", "private"]) {
        mkdirSync(join(root, directory), { recursive: true });
      }
      writeFileSync(join(root, sourceReport), "produced report\n");
      writeFileSync(join(root, "private/keep.json"), "private fixture\n");
      prepare(root);
      const before = privateFixtureState(root);
      const result = spawnSync("bash", ["-euo", "pipefail", "-c", mutationRetentionCommand()], {
        cwd: root,
      });
      assert.notEqual(result.status, 0);
      assert.deepEqual(privateFixtureState(root), before);
      if (kind.startsWith("linked source") || kind === "non-regular source") {
        assert.equal(existsSync(join(root, retainedReport)), false);
      }
      if (kind === "pre-existing destination") {
        assert.equal(readFileSync(join(root, retainedReport), "utf8"), "prior report\n");
      }
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
}
const steps = () => [
  { id: "evidence", run: "node verify.mjs" },
  {
    id: "artifact_scan",
    shell: "bash",
    if: "always()",
    run: "node eng/ci/scan/main.mjs artifacts artifacts/result.json",
  },
  {
    uses: `actions/upload-artifact@${"1".repeat(40)}`,
    if: `${guard} && steps.evidence.outcome == 'success'`,
    with: { path: "artifacts/result.json", "if-no-files-found": "error" },
  },
];
test("uploads may require stronger earlier evidence success while preserving mandatory scanning", () => {
  checkUploads(steps());
  for (const condition of [
    "always() && steps.evidence.outcome == 'success'",
    `${guard} || steps.evidence.outcome == 'success'`,
    `${guard} && steps.absent.outcome == 'success'`,
    `${guard} && steps.evidence.outcome != 'failure'`,
    `${guard} && steps.evidence.outcome == 'success' && steps.evidence.outcome == 'success'`,
  ]) {
    const fixture = steps();
    const upload = fixture.at(-1);
    assert.ok(upload);
    upload.if = condition;
    assert.throws(() => checkUploads(fixture));
  }
});
test("deployment reports can only upload after successful current-attempt schema verification", () => {
  const workflow = parseWorkflow(
    readFileSync(new URL("../../.github/workflows/verify-deployment.yml", import.meta.url), "utf8"),
  ).value;
  /** @type {import("./types.mjs").Json[]} */
  const actual = workflow.jobs.deployment.steps;
  const evidence = actual.find((step) => step.id === "deployment_evidence");
  assert.ok(evidence);
  assert.equal(evidence.run, 'node eng/ci/deployment/report.mjs "$CLAIMCORE_DEPLOYMENT_REPORT"');
  const upload = actual.find((step) => step.uses?.startsWith("actions/upload-artifact@"));
  assert.ok(upload);
  /** @param {string} condition */
  const requireEvidence = (condition) =>
    assert.equal(
      condition,
      `\${{ always() && steps.artifact_scan.outcome == 'success' && steps.job_checkout_current.outcome == 'success' && steps.deployment_evidence.outcome == 'success' }}`,
    );
  requireEvidence(upload.if);
  assert.throws(() =>
    requireEvidence(upload.if.replace(" && steps.deployment_evidence.outcome == 'success'", "")),
  );
  assert.equal(
    upload.with.path,
    `artifacts/deployment/\${{ github.run_id }}-\${{ github.run_attempt }}/result.json`,
  );
});

test("quoted scan paths require exact own step environment bindings to safe artifact paths", () => {
  const path = "artifacts/deployment/current/result.json";
  /** @param {import("./types.mjs").Json} env */
  const bound = (env) => [
    {
      id: "artifact_scan",
      shell: "bash",
      if: "always()",
      env,
      run: 'node eng/ci/scan/main.mjs artifacts "$REPORT_PATH"',
    },
    {
      uses: `actions/upload-artifact@${"1".repeat(40)}`,
      if: guard,
      with: { path, "if-no-files-found": "error" },
    },
  ];
  checkUploads(bound({ REPORT_PATH: path }));
  for (const env of [
    {},
    { REPORT_PATH: "private/key.pem" },
    { REPORT_PATH: "artifacts/../private" },
    { REPORT_PATH: "artifacts/*.json" },
    { REPORT_PATH: "artifacts/result.json\nprivate" },
    { REPORT_PATH: 42 },
  ]) {
    assert.throws(() => checkUploads(bound(env)));
  }
  /** @type {import("./types.mjs").Json} */
  const inherited = {};
  Object.setPrototypeOf(inherited, { REPORT_PATH: path });
  assert.throws(() => checkUploads(bound(inherited)));
});
