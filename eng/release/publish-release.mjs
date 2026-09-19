import { parseArgs } from "node:util";
import { releaseClaimCore } from "./publisher.mjs";

const options = {
  tag: { type: "string" },
  "expected-sha": { type: "string" },
  publish: { type: "boolean", default: false },
};

const assertWorkflowContext = () => {
  if (process.env.GITHUB_ACTIONS !== "true") return;
  if (
    process.env.GITHUB_EVENT_NAME !== "workflow_dispatch" ||
    process.env.GITHUB_REF !== "refs/heads/main"
  ) {
    throw new Error("Release publishing must be manually dispatched from main.");
  }
};

const githubApi = (repository, token) => async (path, { method = "GET", json } = {}) => {
  const response = await fetch(`https://api.github.com/repos/${repository}/${path}`, {
    method,
    redirect: "error",
    signal: AbortSignal.timeout(30_000),
    headers: {
      Accept: "application/vnd.github+json",
      Authorization: `Bearer ${token}`,
      "X-GitHub-Api-Version": "2026-03-10",
      "User-Agent": "claimcore-release-publisher",
      ...(json === undefined ? {} : { "Content-Type": "application/json" }),
    },
    body: json === undefined ? undefined : JSON.stringify(json),
  });
  if (!response.ok) throw new Error(`GitHub ${method} ${path}: HTTP ${response.status}.`);
  return response.json();
};

const main = async () => {
  const { values } = parseArgs({ options, strict: true, allowPositionals: false });
  const repository = process.env.GITHUB_REPOSITORY;
  const token = process.env.GH_TOKEN;
  if (!repository || !token) throw new Error("Set GITHUB_REPOSITORY and GH_TOKEN.");
  assertWorkflowContext();
  const result = await releaseClaimCore({
    repository,
    tag: values.tag,
    expectedSha: values["expected-sha"],
    publish: values.publish,
    api: githubApi(repository, token),
  });
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
};

main().catch((error) => {
  console.error(`Release stopped: ${error.message}`);
  console.error("No automatic rollback or write retry was attempted. Inspect the release before retrying.");
  process.exitCode = 1;
});
