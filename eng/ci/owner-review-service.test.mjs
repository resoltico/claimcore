import test from "node:test";
import assert from "node:assert/strict";
import { ownerReview } from "./owner-review-service.mjs";
const head = "a".repeat(40),
  base = "b".repeat(40),
  merge = "c".repeat(40);
const baseTree = "d".repeat(40),
  mergeTree = "e".repeat(40);
const source = { commit: "f".repeat(40), sha256: "f".repeat(64) };
function fixture() {
  const repo = {
    id: 1,
    full_name: "owner/repo",
    default_branch: "main",
    owner: { type: "User", id: 9 },
  };
  const pr = {
    number: 3,
    state: "open",
    draft: false,
    head: { sha: head, ref: "topic", repo },
    base: { sha: base, ref: "main", repo },
    merge_commit_sha: merge,
    html_url: "https://github.com/owner/repo/pull/3",
  };
  const run = {
    id: 12,
    workflow_id: 5,
    path: ".github/workflows/ci.yml",
    head_sha: head,
    run_attempt: 1,
    status: "completed",
    conclusion: "success",
    event: "pull_request",
    repository: repo,
    head_repository: repo,
    referenced_workflows: [
      {
        path: "owner/repo/.github/workflows/verify-unit.yml",
        ref: "refs/pull/3/merge",
        sha: merge,
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
  const documents = {
    "": repo,
    "pulls/3": pr,
    "git/ref/pull/3/merge": {
      ref: "refs/pull/3/merge",
      object: { type: "commit", sha: merge },
    },
    [`git/commits/${base}`]: { sha: base, tree: { sha: baseTree } },
    [`git/commits/${merge}`]: {
      sha: merge,
      tree: { sha: mergeTree },
      parents: [{ sha: base }, { sha: head }],
    },
    [`git/trees/${baseTree}?recursive=1`]: {
      sha: baseTree,
      truncated: false,
      tree: [],
    },
    [`git/trees/${mergeTree}?recursive=1`]: {
      sha: mergeTree,
      truncated: false,
      tree: [
        { path: "eng/ci/policy.mjs", type: "blob", mode: "100644", sha: head },
      ],
    },
    "actions/workflows/ci.yml": { id: 5, path: ".github/workflows/ci.yml" },
    [`actions/workflows/ci.yml/runs?event=pull_request&head_sha=${head}&per_page=100&page=1`]:
      { total_count: 1, workflow_runs: [run] },
    "actions/runs/12": run,
    "actions/runs/12/attempts/1/jobs?per_page=100&page=1": {
      total_count: 1,
      jobs,
    },
  };
  const requests = [];
  const api = async (path, options) => {
    assert.equal(options, undefined, "Reporter must not write.");
    requests.push(path);
    assert(Object.hasOwn(documents, path), `Unexpected request ${path}`);
    return structuredClone(documents[path]);
  };
  return { documents, requests, api, pr, run, repo };
}
test("owner report binds complete change scope and successful CI without granting approval", async () => {
  const { api, requests } = fixture();
  const report = await ownerReview(api, 3, head, source);
  assert.equal(report.mergeSha, merge);
  assert.equal(report.mergeTree, mergeTree);
  assert.equal(report.ownerId, 9);
  assert.equal(report.ci.qualification, "verified-current-head-ci");
  assert.equal(report.ownerAuthorization, "not-granted-by-this-report");
  assert(report.changes[0].scopes.includes("contract-policy"));
  assert.match(report.reportSha256, /^[0-9a-f]{64}$/u);
  assert(
    !requests.some((path) => path.includes("contents/")),
    "Candidate code is never loaded.",
  );
});
test("draft and pending CI are reportable but are not approval", async () => {
  const f = fixture();
  f.pr.draft = true;
  f.run.status = "waiting";
  f.run.conclusion = null;
  const result = await ownerReview(f.api, 3, head, source);
  assert.equal(result.ci.draft, true);
  assert.equal(result.ci.qualification, "checks-pending-or-approval-required");
  assert.equal(result.ownerAuthorization, "not-granted-by-this-report");
});
test("owner report accepts a null PR merge field only with the exact tested merge ref", async () => {
  const f = fixture();
  f.pr.merge_commit_sha = null;
  const report = await ownerReview(f.api, 3, head, source);
  assert.equal(report.mergeSha, merge);
  assert.equal(report.ci.qualification, "verified-current-head-ci");
});
for (const [label, mutate] of [
  [
    "stale head",
    (f) => {
      f.pr.head.sha = base;
    },
  ],
  [
    "wrong repository",
    (f) => {
      f.pr.base.repo = { ...f.repo, id: 99 };
    },
  ],
  [
    "organization",
    (f) => {
      f.repo.owner.type = "Organization";
    },
  ],
  [
    "closed PR",
    (f) => {
      f.pr.state = "closed";
    },
  ],
  [
    "missing merge ref",
    (f) => {
      f.documents["git/ref/pull/3/merge"].object.sha = null;
    },
  ],
  [
    "wrong merge parent",
    (f) => {
      f.documents[`git/commits/${merge}`].parents[0].sha = head;
    },
  ],
  [
    "truncated tree",
    (f) => {
      f.documents[`git/trees/${mergeTree}?recursive=1`].truncated = true;
    },
  ],
  [
    "incomplete job listing",
    (f) => {
      f.documents[
        "actions/runs/12/attempts/1/jobs?per_page=100&page=1"
      ].total_count = 2;
    },
  ],
])
  test(`owner report refuses ${label}`, async () => {
    const f = fixture();
    mutate(f);
    await assert.rejects(ownerReview(f.api, 3, head, source));
  });
test("concurrent head changes invalidate the entire report", async () => {
  const f = fixture();
  let reads = 0;
  const api = async (path) => {
    if (path === "pulls/3" && ++reads === 2) f.pr.head.sha = base;
    return f.api(path);
  };
  await assert.rejects(ownerReview(api, 3, head, source));
});
test("owner change after inspection invalidates the report", async () => {
  const f = fixture();
  let reads = 0;
  const api = async (path) => {
    if (path === "" && ++reads === 2) f.repo.owner.id = 99;
    return f.api(path);
  };
  await assert.rejects(ownerReview(api, 3, head, source), /owner changed/u);
});
