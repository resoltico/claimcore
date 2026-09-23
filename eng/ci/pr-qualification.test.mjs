import test from "node:test";
import assert from "node:assert/strict";
import { qualifyRun, inspectPr } from "./pr-qualification.mjs";
import { githubApi, pages } from "./github-api.mjs";

function example() {
  const repo = { full_name: "owner/repo" };
  const pr = {
    number: 9,
    head: { sha: "a".repeat(40), ref: "topic", repo },
    base: { sha: "c".repeat(40), ref: "main", repo },
    merge_commit_sha: "b".repeat(40),
    state: "open",
    draft: false,
    html_url: "https://github.com/owner/repo/pull/9",
  };
  const workflow = { id: 5, path: ".github/workflows/ci.yml" };
  const run = {
    id: 12,
    workflow_id: 5,
    path: workflow.path,
    head_sha: pr.head.sha,
    run_attempt: 1,
    status: "completed",
    conclusion: "success",
    event: "pull_request",
    repository: repo,
    head_repository: repo,
    referenced_workflows: [
      {
        path: "owner/repo/.github/workflows/verify-unit.yml",
        ref: "refs/pull/9/merge",
        sha: pr.merge_commit_sha,
      },
    ],
  };
  const jobs = [
    {
      id: 21,
      run_id: 12,
      name: "Gate",
      status: "completed",
      conclusion: "success",
    },
  ];
  return { pr, workflow, run, jobs };
}
for (const [label, mutate] of [
  [
    "manual run",
    ({ run }) => {
      run.event = "workflow_dispatch";
    },
  ],
  [
    "stale head",
    ({ run }) => {
      run.head_sha = "f".repeat(40);
    },
  ],
  [
    "stale tested merge",
    ({ run }) => {
      run.referenced_workflows[0].sha = "f".repeat(40);
    },
  ],
  ["missing Gate", ({ jobs }) => jobs.pop()],
  ["duplicate Gate", ({ jobs }) => jobs.push({ ...jobs[0] })],
  [
    "skipped family",
    ({ jobs }) =>
      jobs.push({ ...jobs[0], name: "Unit", conclusion: "skipped" }),
  ],
])
  test(`PR qualification rejects ${label}`, () => {
    const value = example();
    mutate(value);
    assert.throws(() =>
      qualifyRun(value.pr, value.run, value.jobs, value.workflow),
    );
  });
test("pending and failed PR verification are distinct from qualified CI", () => {
  const { pr, run, jobs, workflow } = example();
  assert.equal(qualifyRun(pr, run, jobs, workflow), "verified-current-head-ci");
  run.status = "waiting";
  assert.equal(
    qualifyRun(pr, run, [], workflow),
    "checks-pending-or-approval-required",
  );
  run.status = "completed";
  run.conclusion = "failure";
  assert.equal(qualifyRun(pr, run, jobs, workflow), "checks-not-successful");
});
test("PR read-back rejects a changed attempt and never fabricates approval", async () => {
  const { pr, run, jobs, workflow } = example();
  let reads = 0;
  const api = async (path) => {
    if (path === "pulls/9") return structuredClone(pr);
    if (path === "actions/workflows/ci.yml") return workflow;
    if (path.includes("/runs?"))
      return { total_count: 1, workflow_runs: [run] };
    if (path.includes("/jobs?")) return { total_count: jobs.length, jobs };
    if (path === "actions/runs/12")
      return { ...run, run_attempt: ++reads > 1 ? 2 : 1 };
    throw new Error("Unexpected read.");
  };
  await assert.rejects(inspectPr(api, 9, pr.head.sha), /attempt changed/u);
});
test("GitHub requests use fixed authority and do not disclose denied response or token", async () => {
  const request = async (url, options) => {
    assert.equal(url, "https://api.github.com/repos/owner/repo/rulesets");
    assert.equal(options.redirect, "error");
    assert.equal(options.headers["X-GitHub-Api-Version"], "2026-03-10");
    return {
      ok: false,
      status: 403,
      json: async () => ({ message: "PRIVATE" }),
    };
  };
  const api = githubApi("owner/repo", "PRIVATE-TOKEN", request);
  await assert.rejects(api("rulesets"), /^Error: GITHUB_HTTP_403$/u);
  await assert.rejects(api("../secrets"));
});
test("paginated workflow searches fail on truncated search scope", async () => {
  await assert.rejects(
    pages(
      async () => ({ total_count: 1001, workflow_runs: [] }),
      "runs",
      "workflow_runs",
    ),
  );
});
