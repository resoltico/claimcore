import assert from "node:assert/strict";
import { pages, pullMergeRevision } from "./github-api.mjs";

const sha = (value) =>
  typeof value === "string" && /^[0-9a-f]{40}$/u.test(value);
const samePr = (left, right) =>
  left.head.sha === right.head.sha &&
  left.base.sha === right.base.sha &&
  left.merge_commit_sha === right.merge_commit_sha &&
  left.state === right.state &&
  left.draft === right.draft;

export function qualifyRun(pr, run, jobs, workflow) {
  assert.equal(
    run.event,
    "pull_request",
    "Manual or push runs cannot substitute for PR verification.",
  );
  assert.equal(
    run.workflow_id,
    workflow.id,
    "Verification workflow identity differs.",
  );
  assert.equal(run.path.split("@")[0], ".github/workflows/ci.yml");
  assert.equal(
    run.head_sha,
    pr.head.sha,
    "CI head differs from the published PR head.",
  );
  assert.equal(run.repository.full_name, pr.base.repo.full_name);
  assert.equal(run.head_repository.full_name, pr.head.repo.full_name);
  if (run.status !== "completed") return "checks-pending-or-approval-required";
  if (run.conclusion !== "success") return "checks-not-successful";
  const referenced = run.referenced_workflows;
  assert(
    sha(pr.merge_commit_sha) &&
      Array.isArray(referenced) &&
      referenced.length > 0,
    "A successful run must identify its tested PR merge revision.",
  );
  assert(
    referenced.every(
      (item) =>
        item.ref === `refs/pull/${pr.number}/merge` &&
        item.sha === pr.merge_commit_sha &&
        item.path.startsWith(`${pr.base.repo.full_name}/.github/workflows/`),
    ),
    "Verification was not run against the current PR merge revision.",
  );
  const gates = jobs.filter((job) => job.name === "Gate");
  assert.equal(gates.length, 1, "Exactly one aggregate Gate is required.");
  assert(
    jobs.every(
      (job) =>
        job.run_id === run.id &&
        job.status === "completed" &&
        job.conclusion === "success",
    ),
    "Every current-attempt job must complete successfully.",
  );
  return "verified-current-head-ci";
}

export async function inspectPr(api, number, expectedHead) {
  assert(
    Number.isSafeInteger(number) && number > 0 && sha(expectedHead),
    "Require a PR number and exact published head.",
  );
  const pr = await api(`pulls/${number}`);
  assert.equal(pr.number, number);
  assert.equal(
    pr.head.sha,
    expectedHead,
    "The PR has changed; inspect its actual head before reporting success.",
  );
  const mergeSha =
    pr.state === "open" ? await pullMergeRevision(api, pr) : null;
  const result = {
    number,
    url: pr.html_url,
    base: pr.base.ref,
    head: pr.head.ref,
    headSha: pr.head.sha,
    baseSha: pr.base.sha,
    mergeSha,
    draft: pr.draft,
    state: pr.state,
    ownerAuthorization: "not-assessed-by-ci",
  };
  if (pr.state !== "open")
    return { ...result, qualification: pr.merged ? "merged" : "closed" };
  const workflow = await api("actions/workflows/ci.yml");
  assert.equal(workflow.path, ".github/workflows/ci.yml");
  const runs = await pages(
    api,
    `actions/workflows/ci.yml/runs?event=pull_request&head_sha=${expectedHead}`,
    "workflow_runs",
  );
  runs.sort((left, right) => right.id - left.id);
  if (!runs.length)
    return { ...result, qualification: "no-pr-verification-run" };
  const run = await api(`actions/runs/${runs[0].id}`);
  assert(Number.isSafeInteger(run.run_attempt) && run.run_attempt > 0);
  const jobs =
    run.status === "completed"
      ? await pages(
          api,
          `actions/runs/${run.id}/attempts/${run.run_attempt}/jobs`,
          "jobs",
        )
      : [];
  const qualification = qualifyRun(
    { ...pr, merge_commit_sha: mergeSha },
    run,
    jobs,
    workflow,
  );
  const refreshed = await api(`actions/runs/${run.id}`);
  assert.equal(
    refreshed.run_attempt,
    run.run_attempt,
    "CI attempt changed during verification.",
  );
  assert.equal(
    refreshed.status,
    run.status,
    "CI status changed; repeat the read-only check.",
  );
  assert.equal(refreshed.conclusion, run.conclusion);
  const refreshedPr = await api(`pulls/${number}`);
  assert(samePr(pr, refreshedPr), "PR changed during verification.");
  assert.equal(
    await pullMergeRevision(api, refreshedPr),
    mergeSha,
    "Tested PR merge revision changed during verification.",
  );
  const latest = await pages(
    api,
    `actions/workflows/ci.yml/runs?event=pull_request&head_sha=${expectedHead}`,
    "workflow_runs",
  );
  assert.equal(
    Math.max(...latest.map((value) => value.id)),
    run.id,
    "A newer verification run appeared.",
  );
  return {
    ...result,
    qualification,
    run: run.id,
    attempt: run.run_attempt,
    ciUrl: run.html_url,
  };
}
