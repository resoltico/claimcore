import assert from "node:assert/strict";
import { paginated, verifiedGate } from "./verification.mjs";
import {
  canonicalNotes,
  declaredVersion,
  extractReleaseBody,
  isSha,
  isVersion,
} from "./policy.mjs";

/** @typedef {import("../ci/types.mjs").Json} Json */
/** @typedef {import("../ci/types.mjs").GithubApi} GithubApi */

/**
 * @typedef {object} Plan
 * @property {string} tag
 * @property {string} commit
 * @property {string} title
 * @property {string} body
 */

/**
 * @param {Json} release
 * @param {Plan} plan
 * @param {boolean} draft
 */
const assertRelease = (release, plan, draft) => {
  assert.equal(release["tag_name"], plan.tag, "Release tag differs.");
  assert.equal(release["name"], plan.title, "Release title differs; no overwrite permitted.");
  assert.equal(
    canonicalNotes(release["body"]),
    plan.body,
    "Release body differs; no overwrite permitted.",
  );
  assert.equal(release["prerelease"], false, "Unexpected prerelease flag.");
  assert.equal(release["draft"], draft, "Unexpected draft state.");
  assert(
    Array.isArray(release["assets"]) && release["assets"].length === 0,
    "Source-only release has assets.",
  );
  assert(Number.isSafeInteger(release["id"]) && release["id"] > 0, "Invalid release ID.");
};

/**
 * @param {GithubApi} api
 * @param {string} path
 * @param {string} commit
 */
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

/**
 * @param {GithubApi} api
 * @param {string} tag
 * @param {string} expectedSha
 */
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

/**
 * @param {GithubApi} api
 * @param {string} expectedSha
 */
const assertMainContains = async (api, expectedSha) => {
  const main = await api("git/ref/heads/main");
  assert.equal(main.object.type, "commit");
  assert(isSha(main.object.sha));
  const comparison = await api(`compare/${expectedSha}...${main.object.sha}`);
  assert.equal(
    comparison.merge_base_commit.sha,
    expectedSha,
    "Release commit is not in main history.",
  );
};

/**
 * @param {unknown} repository
 * @param {unknown} tag
 * @param {unknown} expectedSha
 * @param {unknown} publish
 */
const assertRequest = (repository, tag, expectedSha, publish) => {
  assert(
    typeof repository === "string" &&
      /^[A-Za-z0-9][A-Za-z0-9-]*\/[A-Za-z0-9_.-]+$/u.test(repository) &&
      ![".", ".."].includes(repository.split("/")[1] ?? ""),
    "Expected owner/repository.",
  );
  assert(
    typeof tag === "string" && tag.startsWith("v") && isVersion(tag.slice(1)),
    "Expected vX.Y.Z.",
  );
  assert(isSha(expectedSha), "Expected a full lowercase 40-character commit SHA.");
  assert.equal(typeof publish, "boolean");
};

/**
 * The release a tag corresponds to: title and notes read from the tagged commit.
 * @param {GithubApi} api
 * @param {string} tag
 * @param {string} expectedSha
 * @returns {Promise<Plan>}
 */
const planFor = async (api, tag, expectedSha) => {
  const version = tag.slice(1);
  const plan = {
    tag,
    commit: expectedSha,
    title: `ClaimCore ${version} — source preview`,
    body: extractReleaseBody(await readSource(api, "CHANGELOG.md", expectedSha), version),
  };
  assert.equal(
    declaredVersion(await readSource(api, "Directory.Build.props", expectedSha)),
    version,
  );
  return plan;
};

/**
 * Publish the draft only after re-verifying the tag, the gate and the draft itself.
 * @param {{ api: GithubApi, repository: string, tag: string, expectedSha: string }} request
 * @param {Plan} plan
 * @param {Json | undefined} existing The draft that already exists, if any.
 * @param {() => Promise<void>} unchangedTag
 */
const publishDraft = async (
  { api, repository, tag, expectedSha },
  plan,
  existing,
  unchangedTag,
) => {
  const release =
    existing ??
    (await api("releases", {
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
    }));
  if (existing === undefined) assertRelease(release, plan, true);
  const releasePath = `releases/${release["id"]}`;
  assertRelease(await api(releasePath), plan, true);
  const gate = await verifiedGate(api, repository, tag, expectedSha);
  await unchangedTag();
  assertRelease(await api(releasePath), plan, true);
  await api(releasePath, { method: "PATCH", json: { draft: false, make_latest: "legacy" } });
  const published = await api(releasePath);
  assertRelease(published, plan, false);
  await unchangedTag();
  return { status: "published", ...plan, gate, url: published.html_url };
};

/**
 * Validate a version tag's release and, when asked, publish it as a source-only release.
 * @param {{ repository: unknown, tag: unknown, expectedSha: unknown, api: GithubApi, publish?: unknown }} request
 */
export const releaseClaimCore = async ({ repository, tag, expectedSha, api, publish = false }) => {
  assertRequest(repository, tag, expectedSha, publish);
  const checked = /** @type {{ repository: string, tag: string, expectedSha: string }} */ ({
    repository,
    tag,
    expectedSha,
  });
  const tagObject = await tagCommit(api, checked.tag, checked.expectedSha);
  const unchangedTag = async () =>
    assert.equal(
      await tagCommit(api, checked.tag, checked.expectedSha),
      tagObject,
      "Tag changed during publication.",
    );
  await assertMainContains(api, checked.expectedSha);
  const plan = await planFor(api, checked.tag, checked.expectedSha);
  const releases = await paginated(api, "releases");
  const matches = releases.filter((release) => release["tag_name"] === checked.tag);
  assert(matches.length <= 1, "Multiple releases use the requested tag.");
  const existing = matches[0];
  if (existing !== undefined) {
    assertRelease(existing, plan, existing["draft"]);
    if (!existing["draft"])
      return { status: "already-published", ...plan, url: existing["html_url"] };
  }
  const gate = await verifiedGate(api, checked.repository, checked.tag, checked.expectedSha);
  await unchangedTag();
  if (!publish) return { status: "validated", ...plan, gate };
  return publishDraft({ api, ...checked }, plan, existing, unchangedTag);
};
