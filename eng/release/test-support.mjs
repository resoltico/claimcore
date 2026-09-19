import assert from "node:assert/strict";

export const version = "0.3.0";
export const tag = `v${version}`;
export const sha = "1".repeat(40);
const tagSha = "2".repeat(40);
const mainSha = "3".repeat(40);
export const repository = "resoltico/claimcore";
export const title = `ClaimCore ${version} — source preview`;
export const body = `## [${version}] - 2026-09-19\n\n### Fixed\n\n- Preserve café, punctuation, and **formatting**.  \n- Preserve this line.`;
export const changelog = `# Changelog\n\n## [Unreleased]\n\n- Not released.\n\n${body}\n\n## [0.2.0] - 2026-09-14\n\n- Older change.\n`;
export const props = `<Project><PropertyGroup><Product>ClaimCore</Product><Version>${version}</Version></PropertyGroup></Project>`;

const copy = (value) => structuredClone(value);

const source = (text) => ({
  type: "file",
  encoding: "base64",
  size: Buffer.byteLength(text),
  content: Buffer.from(text).toString("base64"),
});

const page = (items, url) => {
  const number = Number(url.searchParams.get("page"));
  return items.slice((number - 1) * 100, number * 100);
};

export const createFixture = () => {
  const run = {
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
      { path: `${repository}/.github/workflows/verify-quality.yml@${sha}`, ref: `refs/tags/${tag}`, sha },
    ],
  };
  const state = {
    changelog,
    props,
    release: null,
    otherReleases: [],
    tagSha,
    target: sha,
    mergeBase: sha,
    annotated: true,
    runs: [run],
    jobs: [{ name: "Gate", head_sha: sha, status: "completed", conclusion: "success" }],
    calls: [],
    losePost: false,
    losePatch: false,
  };

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

  const api = async (path, { method = "GET", json } = {}) => {
    state.calls.push({ path, method, json: copy(json) });
    const url = new URL(path, "https://example.invalid/");
    const route = url.pathname.slice(1);
    if (method === "POST" && route === "releases") {
      assert.equal(state.release, null, "Duplicate release creation.");
      state.release = { ...release(true), ...copy(json) };
      if (state.losePost) {
        state.losePost = false;
        throw new Error("Lost POST response");
      }
      return copy(state.release);
    }
    if (method === "PATCH" && route === "releases/10") {
      Object.assign(state.release, copy(json));
      if (state.losePatch) {
        state.losePatch = false;
        throw new Error("Lost PATCH response");
      }
      return copy(state.release);
    }
    assert.equal(method, "GET", `Unexpected write: ${method} ${path}`);
    if (route === `git/ref/tags/${tag}`) {
      return { ref: `refs/tags/${tag}`, object: { type: state.annotated ? "tag" : "commit", sha: state.tagSha } };
    }
    if (route === `git/tags/${state.tagSha}`) return { tag, object: { type: "commit", sha: state.target } };
    if (route === "git/ref/heads/main") return { object: { type: "commit", sha: mainSha } };
    if (route === `compare/${sha}...${mainSha}`) return { merge_base_commit: { sha: state.mergeBase } };
    if (route === "contents/Directory.Build.props") {
      assert.equal(url.searchParams.get("ref"), sha);
      return source(state.props);
    }
    if (route === "contents/CHANGELOG.md") {
      assert.equal(url.searchParams.get("ref"), sha);
      return source(state.changelog);
    }
    if (route === "releases") return copy(page([...state.otherReleases, ...(state.release ? [state.release] : [])], url));
    if (route === "releases/10") return copy(state.release);
    if (route === "actions/workflows/ci.yml") return { id: 99, path: ".github/workflows/ci.yml" };
    if (route === "actions/workflows/ci.yml/runs") {
      assert.equal(url.searchParams.get("event"), "push");
      assert.equal(url.searchParams.get("head_sha"), sha);
      assert.equal(url.searchParams.get("branch"), tag);
      return { total_count: state.runs.length, workflow_runs: copy(page(state.runs, url)) };
    }
    const jobs = /^actions\/runs\/(\d+)\/attempts\/(\d+)\/jobs$/u.exec(route);
    if (jobs) return { jobs: copy(page(state.jobs, url)) };
    const run = /^actions\/runs\/(\d+)$/u.exec(route);
    if (run) return copy(state.runs.find((item) => item.id === Number(run[1])));
    throw new Error(`Unexpected API request: ${method} ${path}`);
  };

  return {
    state,
    api,
    options: { repository, tag, expectedSha: sha, api },
    release,
    writes: () => state.calls.filter((call) => call.method !== "GET"),
  };
};
