import { parseArgs } from "node:util";
import { githubApi } from "./github-api.mjs";
import { readSettings, configureSettings } from "./settings-service.mjs";
import { settingsPlan } from "./settings-policy.mjs";

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
  if (process.env.GITHUB_ACTIONS === "true")
    throw new Error(
      "Run repository administration from the owner's authenticated workstation, not a product workflow.",
    );
  if (values.check && values.apply)
    throw new Error("Choose check or apply, not both.");
  const repository = values.repository ?? process.env.GITHUB_REPOSITORY;
  const api = githubApi(repository ?? "", process.env.GH_TOKEN);
  if (values.apply) {
    if (!/^[0-9a-f]{64}$/u.test(values["plan-sha"] ?? ""))
      throw new Error(
        "Apply requires the exact SHA from a reviewed fresh plan.",
      );
    const result = await configureSettings(api, values["plan-sha"], (entry) =>
      console.log(JSON.stringify(entry)),
    );
    console.log(JSON.stringify(result, null, 2));
  } else {
    const plan = settingsPlan(await readSettings(api));
    console.log(
      JSON.stringify(
        { mode: values.check ? "check" : "plan", repository, ...plan },
        null,
        2,
      ),
    );
    if (values.check && plan.operations.length) process.exitCode = 2;
  }
}
main().catch((error) => {
  console.error(`Repository configuration stopped: ${error.message}`);
  console.error(
    "No token or provider response is printed. No automatic rollback or write retry; inspect any confirmed changes and obtain a fresh plan.",
  );
  process.exitCode = 1;
});
