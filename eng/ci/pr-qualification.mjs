import assert from "node:assert/strict";
import { pages, pullMergeRevision } from "./github-api.mjs";

/** @typedef {import("./types.mjs").Json} Json */
/** @typedef {import("./types.mjs").GithubApi} GithubApi */

/** @param {unknown} value @returns {value is string} */
const sha = (value) => typeof value === "string" && /^[0-9a-f]{40}$/u.test(value);

/**
 * @param {Json} left
 * @param {Json} right
 */
const samePr = (left, right) =>
  left["head"].sha === right["head"].sha &&
  left["base"].sha === right["base"].sha &&
  left["merge_commit_sha"] === right["merge_commit_sha"] &&
  left["state"] === right["state"] &&
  left["draft"] === right["draft"];

/**
 * @param {Json} pr
 * @param {Json} run
 * @param {Json} workflow
 */
function assertRunIdentity(pr, run, workflow) {
  assert.equal(
    run["event"],
    "pull_request",
    "Manual or push runs cannot substitute for PR verification.",
  );
  assert.equal(run["workflow_id"], workflow["id"], "Verification workflow identity differs.");
  assert.equal(run["path"].split("@")[0], ".github/workflows/ci.yml");
  assert.equal(run["head_sha"], pr["head"].sha, "CI head differs from the published PR head.");
  assert.equal(run["repository"].full_name, pr["base"].repo.full_name);
  assert.equal(run["head_repository"].full_name, pr["head"].repo.full_name);
}

/**
 * @param {Json} pr
 * @param {Json} run
 * @param {Json[]} jobs
 */
function assertTestedMerge(pr, run, jobs) {
  const referenced = run["referenced_workflows"];
  assert(
    sha(pr["merge_commit_sha"]) && Array.isArray(referenced) && referenced.length > 0,
    "A successful run must identify its tested PR merge revision.",
  );
  assert(
    referenced.every(
      (/** @type {Json} */ item) =>
        item.ref === `refs/pull/${pr["number"]}/merge` &&
        item.sha === pr["merge_commit_sha"] &&
        item.path.startsWith(`${pr["base"].repo.full_name}/.github/workflows/`),
    ),
    "Verification was not run against the current PR merge revision.",
  );
  assert.equal(
    jobs.filter((job) => job["name"] === "Gate").length,
    1,
    "Exactly one aggregate Gate is required.",
  );
  assert(
    jobs.every(
      (job) =>
        job["run_id"] === run["id"] &&
        job["status"] === "completed" &&
        job["conclusion"] === "success",
    ),
    "Every current-attempt job must complete successfully.",
  );
}

/**
 * How well a CI run verifies the pull request's tested merge.
 * @param {Json} pr
 * @param {Json} run
 * @param {Json[]} jobs
 * @param {Json} workflow
 */
export function qualifyRun(pr, run, jobs, workflow) {
  assertRunIdentity(pr, run, workflow);
  if (run["status"] !== "completed") {
    return "checks-pending-or-approval-required";
  }
  if (run["conclusion"] !== "success") {
    return "checks-not-successful";
  }
  assertTestedMerge(pr, run, jobs);
  return "verified-current-head-ci";
}

/**
 * The tested merge commit of an open pull request, checked to contain its current base and head.
 * @param {GithubApi} api
 * @param {Json} pr
 * @returns {Promise<string | null>}
 */
async function testedMerge(api, pr) {
  if (pr["state"] !== "open") {
    return null;
  }
  const mergeSha = await pullMergeRevision(api, pr);
  const commit = await api(`git/commits/${mergeSha}`);
  assert(
    commit.sha === mergeSha &&
      commit.parents?.length === 2 &&
      commit.parents[0].sha === pr["base"].sha &&
      commit.parents[1].sha === pr["head"].sha,
    "Tested PR merge does not contain the current base and head.",
  );
  return mergeSha;
}

/** @param {GithubApi} api @param {string} head */
const verificationRuns = (api, head) =>
  pages(api, `actions/workflows/ci.yml/runs?event=pull_request&head_sha=${head}`, "workflow_runs");

/**
 * Prove nothing moved while the verification was read.
 * @param {GithubApi} api
 * @param {{ pr: Json, run: Json, number: number, mergeSha: string | null, expectedHead: string }} read
 */
async function assertStable(api, { pr, run, number, mergeSha, expectedHead }) {
  const refreshed = await api(`actions/runs/${run["id"]}`);
  assert.equal(
    refreshed.run_attempt,
    run["run_attempt"],
    "CI attempt changed during verification.",
  );
  assert.equal(refreshed.status, run["status"], "CI status changed; repeat the read-only check.");
  assert.equal(refreshed.conclusion, run["conclusion"]);
  const refreshedPr = await api(`pulls/${number}`);
  assert(samePr(pr, refreshedPr), "PR changed during verification.");
  assert.equal(
    await pullMergeRevision(api, refreshedPr),
    mergeSha,
    "Tested PR merge revision changed during verification.",
  );
  const latest = await verificationRuns(api, expectedHead);
  assert.equal(
    Math.max(...latest.map((value) => value["id"])),
    run["id"],
    "A newer verification run appeared.",
  );
}

/**
 * Report how the current head of a pull request stands against its CI, changing nothing.
 * @param {GithubApi} api
 * @param {number} number
 * @param {string} expectedHead
 */
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
  const mergeSha = await testedMerge(api, pr);
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
  if (pr.state !== "open") {
    return { ...result, qualification: pr.merged ? "merged" : "closed" };
  }
  const workflow = await api("actions/workflows/ci.yml");
  assert.equal(workflow.path, ".github/workflows/ci.yml");
  const runs = await verificationRuns(api, expectedHead);
  runs.sort((left, right) => right["id"] - left["id"]);
  const [newest] = runs;
  if (!newest) {
    return { ...result, qualification: "no-pr-verification-run" };
  }
  const run = await api(`actions/runs/${newest["id"]}`);
  assert(Number.isSafeInteger(run.run_attempt) && run.run_attempt > 0);
  const jobs =
    run.status === "completed"
      ? await pages(api, `actions/runs/${run.id}/attempts/${run.run_attempt}/jobs`, "jobs")
      : [];
  const qualification = qualifyRun({ ...pr, merge_commit_sha: mergeSha }, run, jobs, workflow);
  await assertStable(api, { pr, run, number, mergeSha, expectedHead });
  return { ...result, qualification, run: run.id, attempt: run.run_attempt, ciUrl: run.html_url };
}
