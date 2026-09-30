import { parseArgs } from "node:util";
import { githubApi } from "./github-api.mjs";
import { readSettings, configureSettings } from "./settings-service.mjs";
import { settingsPlan } from "./settings-policy.mjs";

/**
 * @param {import("./types.mjs").GithubApi} api
 * @param {string} planSha
 */
async function apply(api, planSha) {
  if (!/^[0-9a-f]{64}$/u.test(planSha)) {
    throw new Error("Apply requires the exact SHA from a reviewed fresh plan.");
  }
  const result = await configureSettings(api, planSha, (entry) =>
    console.log(JSON.stringify(entry)),
  );
  console.log(JSON.stringify(result, null, 2));
}

/**
 * @param {import("./types.mjs").GithubApi} api
 * @param {boolean} check
 */
async function plan(api, check) {
  const current = settingsPlan(await readSettings(api));
  console.log(JSON.stringify({ mode: check ? "check" : "plan", ...current }, null, 2));
  if (check && current.operations.length > 0) {
    process.exitCode = 2;
  }
}

async function main() {
  const { values } = parseArgs({
    options: {
      repository: { type: "string" },
      check: { type: "boolean" },
      apply: { type: "boolean" },
      "plan-sha": { type: "string" },
    },
    strict: true,
    allowPositionals: false,
  });
  if (process.env["GITHUB_ACTIONS"] === "true") {
    throw new Error(
      "Run repository administration from the owner's authenticated workstation, not a product workflow.",
    );
  }
  if (values.check && values.apply) {
    throw new Error("Choose check or apply, not both.");
  }
  const repository = values.repository ?? process.env["GITHUB_REPOSITORY"];
  const api = githubApi(repository ?? "", process.env["GH_TOKEN"]);
  if (values.apply) {
    await apply(api, values["plan-sha"] ?? "");
  } else {
    await plan(api, Boolean(values.check));
  }
}
main().catch((error) => {
  console.error(
    `Repository configuration stopped: ${error instanceof Error ? error.message : "unknown"}`,
  );
  console.error(
    "No token or provider response is printed. No automatic rollback or write retry; inspect any confirmed changes and obtain a fresh plan.",
  );
  process.exitCode = 1;
});
