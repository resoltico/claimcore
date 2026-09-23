import { parseArgs } from "node:util";
import { fileURLToPath } from "node:url";
import { githubApi } from "./github-api.mjs";
import { ownerReview } from "./owner-review-service.mjs";
import { toolProvenance } from "./owner-tool-source.mjs";
import assert from "node:assert/strict";

async function main() {
  const { values } = parseArgs({
    options: {
      repository: { type: "string" },
      pr: { type: "string" },
      head: { type: "string" },
    },
    strict: true,
    allowPositionals: false,
  });
  if (!/^[1-9]\d*$/u.test(values.pr ?? ""))
    throw new Error("Require a canonical PR number.");
  const root = fileURLToPath(new URL("../../", import.meta.url));
  const source = toolProvenance(root);
  const report = await ownerReview(
    githubApi(values.repository ?? "", process.env.GH_TOKEN),
    Number(values.pr),
    values.head,
    source,
  );
  assert.deepEqual(
    toolProvenance(root),
    source,
    "Reporting-tool source changed during inspection.",
  );
  console.log(JSON.stringify(report, null, 2));
  if (report.ci.qualification !== "verified-current-head-ci" || report.ci.draft)
    process.exitCode = 2;
}
main().catch(() => {
  console.error(
    "Owner-review report could not be established. Use clean reviewed tooling, authenticated read access and exact current PR revisions. No approval, settings change or merge was performed.",
  );
  process.exitCode = 1;
});
