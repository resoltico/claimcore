import assert from "node:assert/strict";
import { canonicalNotes, declaredVersion, extractReleaseBody, isSha, isVersion } from "./policy.mjs";

const workflowPath = ".github/workflows/ci.yml";

const assertRelease = (release, plan, draft) => {
  assert.equal(release.tag_name, plan.tag, "Release tag differs.");
  assert.equal(release.name, plan.title, "Release title differs; no overwrite permitted.");
  assert.equal(canonicalNotes(release.body), plan.body, "Release body differs; no overwrite permitted.");
  assert.equal(release.prerelease, false, "Unexpected prerelease flag.");
  assert.equal(release.draft, draft, "Unexpected draft state.");
  assert(Array.isArray(release.assets) && release.assets.length === 0, "Source-only release has assets.");
  assert(Number.isSafeInteger(release.id) && release.id > 0, "Invalid release ID.");
};

const paginated = async (api, path, field) => {
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

const readSource = async (api, path, commit) => {
  const file = await api(`contents/${path}?ref=${commit}`);
  assert(
    file.type === "file" && file.encoding === "base64" && file.size <= 1_000_000,
    "Expected a small base64-encoded source file.",
  );
  const encoded = file.content.replace(/\s/gu, "");
  const bytes = Buffer.from(encoded, "base64");
  assert.equal(bytes.toString("base64"), encoded, "Invalid base64 source.");
  assert.equal(bytes.length, file.size, "Source length differs.");
  return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
};

const tagCommit = async (api, tag, expectedSha) => {
  const reference = await api(`git/ref/tags/${tag}`);
  assert.equal(reference.ref, `refs/tags/${tag}`, "Unexpected tag reference.");
  assert.equal(reference.object.type, "tag", "An annotated tag is required.");
  assert(isSha(reference.object.sha), "Invalid tag-object SHA.");
  const annotation = await api(`git/tags/${reference.object.sha}`);
  assert.equal(annotation.tag, tag, "Annotation names a different tag.");
  assert.equal(annotation.object.type, "commit", "Tag must directly annotate a commit.");
  assert.equal(annotation.object.sha, expectedSha, "Tag does not identify the expected commit.");
  return reference.object.sha;
};

const assertMainContains = async (api, expectedSha) => {
  const main = await api("git/ref/heads/main");
  assert.equal(main.object.type, "commit");
  assert(isSha(main.object.sha));
  const comparison = await api(`compare/${expectedSha}...${main.object.sha}`);
  assert.equal(comparison.merge_base_commit.sha, expectedSha, "Release commit is not in main history.");
};

const verifiedGate = async (api, repository, tag, expectedSha) => {
  const workflow = await api("actions/workflows/ci.yml");
  assert.equal(workflow.path, workflowPath, "Unexpected verification workflow.");
  assert(Number.isSafeInteger(workflow.id) && workflow.id > 0, "Invalid workflow ID.");
  const query = new URLSearchParams({ event: "push", head_sha: expectedSha, branch: tag });
  const runs = await paginated(api, `actions/workflows/ci.yml/runs?${query}`, "workflow_runs");
  assert(runs.length > 0, "No tag-push verification run exists.");
  runs.sort((left, right) => Date.parse(right.created_at) - Date.parse(left.created_at) || right.id - left.id);
  const runPath = `actions/runs/${runs[0].id}`;
  const run = await api(runPath);
  assert.equal(run.workflow_id, workflow.id, "Run belongs to a different workflow.");
  assert.equal(run.path.split("@")[0], workflowPath, "Run uses a different workflow path.");
  assert.equal(run.repository.full_name.toLowerCase(), repository.toLowerCase());
  assert.equal(run.head_repository.full_name.toLowerCase(), repository.toLowerCase());
  assert.equal(run.head_sha, expectedSha);
  assert.equal(run.head_branch, tag);
  assert.equal(run.event, "push");
  assert.equal(run.status, "completed", "Newest verification run is not complete.");
  assert.equal(run.conclusion, "success", "Newest verification run failed.");
  assert(Number.isSafeInteger(run.run_attempt) && run.run_attempt > 0, "Invalid run attempt.");
  assert(
    Array.isArray(run.referenced_workflows) &&
      run.referenced_workflows.some(
        (item) =>
          item.path.startsWith(`${repository}/.github/workflows/`) &&
          item.ref === `refs/tags/${tag}` &&
          item.sha === expectedSha,
      ),
    "Run is not bound to the release tag.",
  );
  const jobs = await paginated(api, `${runPath}/attempts/${run.run_attempt}/jobs`, "jobs");
  const gates = jobs.filter((job) => job.name === "Gate");
  assert.equal(gates.length, 1, "Expected exactly one aggregate Gate job.");
  assert.equal(gates[0].head_sha, expectedSha);
  assert.equal(gates[0].status, "completed");
  assert.equal(gates[0].conclusion, "success", "Aggregate Gate did not succeed.");
  const refreshed = await api(runPath);
  assert.equal(refreshed.run_attempt, run.run_attempt, "CI was rerun during verification.");
  assert.equal(refreshed.status, "completed");
  assert.equal(refreshed.conclusion, "success");
  return { run: run.id, attempt: run.run_attempt };
};

export const releaseClaimCore = async ({ repository, tag, expectedSha, api, publish = false }) => {
  assert(
    typeof repository === "string" &&
      /^[A-Za-z0-9][A-Za-z0-9-]*\/[A-Za-z0-9_.-]+$/u.test(repository) &&
      ![".", ".."].includes(repository.split("/")[1]),
    "Expected owner/repository.",
  );
  assert(typeof tag === "string" && tag.startsWith("v") && isVersion(tag.slice(1)), "Expected vX.Y.Z.");
  assert(isSha(expectedSha), "Expected a full lowercase 40-character commit SHA.");
  assert.equal(typeof publish, "boolean");

  const version = tag.slice(1);
  const tagObject = await tagCommit(api, tag, expectedSha);
  const unchangedTag = async () =>
    assert.equal(await tagCommit(api, tag, expectedSha), tagObject, "Tag changed during publication.");
  await assertMainContains(api, expectedSha);
  const plan = {
    tag,
    commit: expectedSha,
    title: `ClaimCore ${version} — source preview`,
    body: extractReleaseBody(await readSource(api, "CHANGELOG.md", expectedSha), version),
  };
  assert.equal(declaredVersion(await readSource(api, "Directory.Build.props", expectedSha)), version);

  const releases = await paginated(api, "releases");
  const matches = releases.filter((release) => release.tag_name === tag);
  assert(matches.length <= 1, "Multiple releases use the requested tag.");
  let release = matches[0];
  if (release !== undefined) {
    assertRelease(release, plan, release.draft);
    if (!release.draft) return { status: "already-published", ...plan, url: release.html_url };
  }

  let gate = await verifiedGate(api, repository, tag, expectedSha);
  await unchangedTag();
  if (!publish) return { status: "validated", ...plan, gate };

  if (release === undefined) {
    release = await api("releases", {
      method: "POST",
      json: {
        tag_name: tag,
        target_commitish: expectedSha,
        name: plan.title,
        body: plan.body,
        draft: true,
        prerelease: false,
        generate_release_notes: false,
        make_latest: "false",
      },
    });
    assertRelease(release, plan, true);
  }

  const releasePath = `releases/${release.id}`;
  assertRelease(await api(releasePath), plan, true);
  gate = await verifiedGate(api, repository, tag, expectedSha);
  await unchangedTag();
  assertRelease(await api(releasePath), plan, true);
  await api(releasePath, { method: "PATCH", json: { draft: false, make_latest: "legacy" } });
  const published = await api(releasePath);
  assertRelease(published, plan, false);
  await unchangedTag();
  return { status: "published", ...plan, gate, url: published.html_url };
};
