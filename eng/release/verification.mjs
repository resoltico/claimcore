import assert from "node:assert/strict";

/** @typedef {import("../ci/types.mjs").Json} Json */
/** @typedef {import("../ci/types.mjs").GithubApi} GithubApi */

export const workflowPath = ".github/workflows/ci.yml";

/**
 * @param {GithubApi} api
 * @param {string} path
 * @param {string} [field]
 * @returns {Promise<Json[]>}
 */
export const paginated = async (api, path, field) => {
  /** @type {Json[]} */
  const values = [];
  for (let page = 1; page <= 100; page += 1) {
    const response = await api(`${path}${path.includes("?") ? "&" : "?"}per_page=100&page=${page}`);
    if (field === "workflow_runs") {
      assert(response.total_count <= 1000, "Workflow search exceeds GitHub result limit.");
    }
    const items = field === undefined ? response : response[field];
    assert(Array.isArray(items), "Malformed paginated GitHub response.");
    values.push(...items);
    if (items.length < 100) return values;
  }
  throw new Error("Pagination limit reached; refusing an incomplete result.");
};

/**
 * The newest tag-push run must be this workflow, repository, commit and tag, completed and green.
 * @param {Json} run
 * @param {Json} workflow
 * @param {{ repository: string, tag: string, expectedSha: string }} release
 */
const assertRun = (run, workflow, { repository, tag, expectedSha }) => {
  assert.equal(run["workflow_id"], workflow["id"], "Run belongs to a different workflow.");
  assert.equal(run["path"].split("@")[0], workflowPath, "Run uses a different workflow path.");
  assert.equal(run["repository"].full_name.toLowerCase(), repository.toLowerCase());
  assert.equal(run["head_repository"].full_name.toLowerCase(), repository.toLowerCase());
  assert.equal(run["head_sha"], expectedSha);
  assert.equal(run["head_branch"], tag);
  assert.equal(run["event"], "push");
  assert.equal(run["status"], "completed", "Newest verification run is not complete.");
  assert.equal(run["conclusion"], "success", "Newest verification run failed.");
  assert(
    Number.isSafeInteger(run["run_attempt"]) && run["run_attempt"] > 0,
    "Invalid run attempt.",
  );
  assert(
    Array.isArray(run["referenced_workflows"]) &&
      run["referenced_workflows"].some(
        (/** @type {Json} */ item) =>
          item.path.startsWith(`${repository}/.github/workflows/`) &&
          item.ref === `refs/tags/${tag}` &&
          item.sha === expectedSha,
      ),
    "Run is not bound to the release tag.",
  );
};

/**
 * @param {Json[]} jobs
 * @param {string} expectedSha
 */
const assertGate = (jobs, expectedSha) => {
  const gates = jobs.filter((job) => job["name"] === "Gate");
  assert.equal(gates.length, 1, "Expected exactly one aggregate Gate job.");
  const gate = /** @type {Json} */ (gates[0]);
  assert.equal(gate["head_sha"], expectedSha);
  assert.equal(gate["status"], "completed");
  assert.equal(gate["conclusion"], "success", "Aggregate Gate did not succeed.");
};

/**
 * @param {GithubApi} api
 * @param {string} repository
 * @param {string} tag
 * @param {string} expectedSha
 */
export const verifiedGate = async (api, repository, tag, expectedSha) => {
  const workflow = await api("actions/workflows/ci.yml");
  assert.equal(workflow.path, workflowPath, "Unexpected verification workflow.");
  assert(Number.isSafeInteger(workflow.id) && workflow.id > 0, "Invalid workflow ID.");
  const query = new URLSearchParams({ event: "push", head_sha: expectedSha, branch: tag });
  const runs = await paginated(api, `actions/workflows/ci.yml/runs?${query}`, "workflow_runs");
  assert(runs.length > 0, "No tag-push verification run exists.");
  runs.sort(
    (left, right) =>
      Date.parse(right["created_at"]) - Date.parse(left["created_at"]) || right["id"] - left["id"],
  );
  const runPath = `actions/runs/${/** @type {Json} */ (runs[0])["id"]}`;
  const run = await api(runPath);
  assertRun(run, workflow, { repository, tag, expectedSha });
  assertGate(
    await paginated(api, `${runPath}/attempts/${run.run_attempt}/jobs`, "jobs"),
    expectedSha,
  );
  const refreshed = await api(runPath);
  assert.equal(refreshed.run_attempt, run.run_attempt, "CI was rerun during verification.");
  assert.equal(refreshed.status, "completed");
  assert.equal(refreshed.conclusion, "success");
  return { run: run.id, attempt: run.run_attempt };
};
