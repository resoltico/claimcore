import assert from "node:assert/strict";
import { pullMergeRevision } from "./github-api.mjs";
import { inspectPr } from "./pr-qualification.mjs";
import {
  changedFiles,
  digest,
  isSha,
  reviewQuestions,
  treeFiles,
} from "./owner-review-scope.mjs";

const refIdentity = (ref) => ({
  sha: ref.sha,
  ref: ref.ref,
  repoId: ref.repo.id,
  repository: ref.repo.full_name,
});
const identity = (pr) => ({
  number: pr.number,
  base: refIdentity(pr.base),
  head: refIdentity(pr.head),
  merge: pr.merge_commit_sha,
  state: pr.state,
  draft: pr.draft,
  autoMerge: pr.auto_merge ?? null,
});
async function readCommit(api, sha) {
  assert(isSha(sha), "A current tested merge commit is required.");
  const commit = await api(`git/commits/${sha}`);
  assert(
    commit.sha === sha && isSha(commit.tree?.sha),
    "Review commit identity differs.",
  );
  return commit;
}

export async function ownerReview(api, number, expectedHead, toolSource) {
  assert(
    Number.isSafeInteger(number) && number > 0 && isSha(expectedHead),
    "Require exact PR identity.",
  );
  assert(
    isSha(toolSource?.commit) && /^[0-9a-f]{64}$/u.test(toolSource?.sha256),
    "Require verified tool provenance.",
  );
  const repository = await api("");
  assert(
    repository.owner?.type === "User" && repository.default_branch === "main",
    "Unsupported ownership model.",
  );
  assert(
    Number.isSafeInteger(repository.id) &&
      repository.id > 0 &&
      Number.isSafeInteger(repository.owner.id) &&
      repository.owner.id > 0,
    "Repository identity is missing.",
  );
  const pr = await api(`pulls/${number}`);
  assert(
    pr.number === number && pr.state === "open",
    "Review requires an open PR.",
  );
  assert(
    pr.head.sha === expectedHead && pr.base.ref === "main",
    "Review revisions differ.",
  );
  assert(
    pr.base.repo.id === repository.id &&
      pr.base.repo.full_name === repository.full_name,
    "Review repository differs.",
  );
  const mergeSha = await pullMergeRevision(api, pr);
  const base = await readCommit(api, pr.base.sha);
  const merge = await readCommit(api, mergeSha);
  assert.deepEqual(
    merge.parents?.map((parent) => parent.sha),
    [pr.base.sha, pr.head.sha],
    "Tested merge does not contain the exact current base and head.",
  );
  const trees = await Promise.all(
    [base, merge].map(async (commit) =>
      treeFiles(
        await api(`git/trees/${commit.tree.sha}?recursive=1`),
        commit.tree.sha,
      ),
    ),
  );
  const changes = changedFiles(...trees);
  assert(changes.length > 0, "No proposed source changes to review.");
  const ci = await inspectPr(api, number, expectedHead);
  assert(
    ci.baseSha === pr.base.sha &&
      ci.mergeSha === mergeSha &&
      ci.state === "open" &&
      ci.draft === pr.draft,
    "PR changed while collecting review scope.",
  );
  assert.deepEqual(
    identity(await api(`pulls/${number}`)),
    identity(pr),
    "Review revisions changed.",
  );
  assert.equal(
    await pullMergeRevision(api, pr),
    mergeSha,
    "Review merge revision changed.",
  );
  const currentRepository = await api("");
  for (const key of ["id", "full_name", "default_branch"])
    assert.equal(currentRepository[key], repository[key]);
  assert.equal(
    currentRepository.owner.id,
    repository.owner.id,
    "Review owner changed.",
  );
  const report = {
    schemaVersion: 1,
    repository: repository.full_name,
    repositoryId: repository.id,
    ownerId: repository.owner.id,
    pr: number,
    baseSha: pr.base.sha,
    headSha: pr.head.sha,
    mergeSha,
    baseTree: base.tree.sha,
    mergeTree: merge.tree.sha,
    toolSource,
    changes,
    reviewQuestions,
    ci,
    ownerAuthorization: "not-granted-by-this-report",
    nativeSettings: "not-assessed-by-this-report",
    identitySeparation:
      "owner-credentials-shared-with-an-agent-are-not-independent",
  };
  return { ...report, reportSha256: digest(report) };
}
