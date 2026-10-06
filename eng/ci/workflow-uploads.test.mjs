import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { checkUploads } from "./workflow-uploads.mjs";
import { parseWorkflow } from "./yaml.mjs";

const guard = "always() && steps.artifact_scan.outcome == 'success'";
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
      `\${{ always() && steps.artifact_scan.outcome == 'success' && steps.deployment_evidence.outcome == 'success' }}`,
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
