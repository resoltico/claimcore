import assert from "node:assert/strict";

export const version = "0.3.0";
export const tag = `v${version}`;
export const sha = "1".repeat(40);
const tagSha = "2".repeat(40);
const mainSha = "3".repeat(40);
export const repository = "resoltico/claimcore";
export const title = `ClaimCore ${version} — source preview`;
export const body = `### Fixed\n\n- Preserve café, punctuation, and **formatting**.  \n- Preserve this line.`;
export const section = `## [${version}] - 2026-09-19\n\n${body}`;
export const changelog = `# Changelog\n\n## [Unreleased]\n\n- Not released.\n\n${section}\n\n## [0.2.0] - 2026-09-14\n\n- Older change.\n`;
export const props = `<Project><PropertyGroup><Product>ClaimCore</Product><Version>${version}</Version></PropertyGroup></Project>`;

/** @typedef {import("../ci/types.mjs").Json} Json */

/** @template T @param {T} value @returns {T} */
const copy = (value) => structuredClone(value);

/** @param {string} text */
const source = (text) => ({
  type: "file",
  encoding: "base64",
  size: Buffer.byteLength(text),
  content: Buffer.from(text).toString("base64"),
});

/** @param {Json[]} items @param {URL} url */
const page = (items, url) => {
  const number = Number(url.searchParams.get("page"));
  return items.slice((number - 1) * 100, number * 100);
};

const workflowRun = () => ({
  id: 7,
  workflow_id: 99,
  path: ".github/workflows/ci.yml",
  created_at: "2026-09-19T00:00:00Z",
  head_sha: sha,
  head_branch: tag,
  event: "push",
  status: "completed",
  conclusion: "success",
  run_attempt: 1,
  repository: { full_name: repository },
  head_repository: { full_name: repository },
  referenced_workflows: [
    {
      path: `${repository}/.github/workflows/verify-quality.yml@${sha}`,
      ref: `refs/tags/${tag}`,
      sha,
    },
  ],
});

/** @returns {Json} */
const initialState = () => ({
  changelog,
  props,
  release: null,
  otherReleases: [],
  tagSha,
  target: sha,
  mergeBase: sha,
  annotated: true,
  runs: [workflowRun()],
  jobs: [{ name: "Gate", head_sha: sha, status: "completed", conclusion: "success" }],
  calls: [],
  losePost: false,
  losePatch: false,
});

/** @param {boolean} draft */
const release = (draft) => ({
  id: 10,
  tag_name: tag,
  name: title,
  body,
  draft,
  prerelease: false,
  assets: [],
  html_url: `https://github.com/${repository}/releases/tag/${tag}`,
});

/**
 * Apply one write to the fake repository.
 * @param {Json} state
 * @param {string} method
 * @param {string} route
 * @param {unknown} json
 * @returns {Json | undefined} The response, or undefined when the route is not a write.
 */
function write(state, method, route, json) {
  if (method === "POST" && route === "releases") {
    assert.equal(state["release"], null, "Duplicate release creation.");
    state["release"] = { ...release(true), ...copy(/** @type {Json} */ (json)) };
    if (state["losePost"]) {
      state["losePost"] = false;
      throw new Error("Lost POST response");
    }
    return copy(state["release"]);
  }
  if (method === "PATCH" && route === "releases/10") {
    Object.assign(state["release"], copy(/** @type {Json} */ (json)));
    if (state["losePatch"]) {
      state["losePatch"] = false;
      throw new Error("Lost PATCH response");
    }
    return copy(state["release"]);
  }
  return undefined;
}

/**
 * The git and content documents of the fake repository.
 * @param {Json} state
 * @param {string} route
 * @param {URL} url
 * @returns {Json | undefined}
 */
function readGit(state, route, url) {
  if (route === `git/ref/tags/${tag}`) {
    return {
      ref: `refs/tags/${tag}`,
      object: { type: state["annotated"] ? "tag" : "commit", sha: state["tagSha"] },
    };
  }
  if (route === `git/tags/${state["tagSha"]}`) {
    return { tag, object: { type: "commit", sha: state["target"] } };
  }
  if (route === "git/ref/heads/main") {
    return { object: { type: "commit", sha: mainSha } };
  }
  if (route === `compare/${sha}...${mainSha}`) {
    return { merge_base_commit: { sha: state["mergeBase"] } };
  }
  if (route === "contents/Directory.Build.props" || route === "contents/CHANGELOG.md") {
    assert.equal(url.searchParams.get("ref"), sha);
    return source(state[route === "contents/CHANGELOG.md" ? "changelog" : "props"]);
  }
  return undefined;
}

/**
 * The release and Actions documents of the fake repository.
 * @param {Json} state
 * @param {string} route
 * @param {URL} url
 * @returns {Json | undefined}
 */
function readActions(state, route, url) {
  if (route === "releases") {
    return copy(
      page([...state["otherReleases"], ...(state["release"] ? [state["release"]] : [])], url),
    );
  }
  if (route === "releases/10") {
    return copy(state["release"]);
  }
  if (route === "actions/workflows/ci.yml") {
    return { id: 99, path: ".github/workflows/ci.yml" };
  }
  if (route === "actions/workflows/ci.yml/runs") {
    assert.equal(url.searchParams.get("event"), "push");
    assert.equal(url.searchParams.get("head_sha"), sha);
    assert.equal(url.searchParams.get("branch"), tag);
    return { total_count: state["runs"].length, workflow_runs: copy(page(state["runs"], url)) };
  }
  if (/^actions\/runs\/(\d+)\/attempts\/(\d+)\/jobs$/u.test(route)) {
    return { jobs: copy(page(state["jobs"], url)) };
  }
  const run = /^actions\/runs\/(\d+)$/u.exec(route);
  if (run) {
    return copy(state["runs"].find((/** @type {Json} */ item) => item["id"] === Number(run[1])));
  }
  return undefined;
}

export const createFixture = () => {
  const state = initialState();
  /** @type {import("../ci/types.mjs").GithubApi} */
  const api = async (path = "", { method = "GET", json } = {}) => {
    state["calls"].push({ path, method, json: copy(json) });
    const url = new URL(path, "https://example.invalid/");
    const route = url.pathname.slice(1);
    const written = write(state, method, route, json);
    if (written !== undefined) {
      return written;
    }
    assert.equal(method, "GET", `Unexpected write: ${method} ${path}`);
    const document = readGit(state, route, url) ?? readActions(state, route, url);
    if (document === undefined) {
      throw new Error(`Unexpected API request: ${method} ${path}`);
    }
    return document;
  };

  return {
    state,
    api,
    options: { repository, tag, expectedSha: sha, api },
    release,
    writes: () => state["calls"].filter((/** @type {Json} */ call) => call["method"] !== "GET"),
  };
};
