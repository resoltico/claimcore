import { parseArgs } from "node:util";
import { githubApi } from "./github-api.mjs";
import { inspectPr } from "./pr-qualification.mjs";

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
  const result = await inspectPr(
    githubApi(values.repository ?? "", process.env.GH_TOKEN),
    Number(values.pr),
    values.head,
  );
  console.log(JSON.stringify(result, null, 2));
  if (result.qualification !== "verified-current-head-ci") process.exitCode = 2;
}
main().catch(() => {
  console.error(
    "PR qualification could not be established. Check authenticated repository access and the actual head, merge revision and CI attempt. No write was performed.",
  );
  process.exitCode = 1;
});
