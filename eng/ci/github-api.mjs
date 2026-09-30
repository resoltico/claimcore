/** @typedef {import("./types.mjs").Json} Json */
/** @typedef {import("./types.mjs").GithubApi} GithubApi */

/**
 * @param {string} repository
 * @param {string} token
 * @param {typeof fetch} request
 * @param {string} path
 * @param {{ method: string, json?: unknown }} options
 */
async function send(repository, token, request, path, { method, json }) {
  if (path.startsWith("/") || path.includes("..") || path.includes("://"))
    throw new Error("GITHUB_PATH_REFUSED");
  try {
    return await request(`https://api.github.com/repos/${repository}${path ? `/${path}` : ""}`, {
      method,
      redirect: "error",
      signal: AbortSignal.timeout(30000),
      headers: {
        Accept: "application/vnd.github+json",
        Authorization: `Bearer ${token}`,
        "X-GitHub-Api-Version": "2026-03-10",
        "User-Agent": "claimcore-governance",
        ...(json === undefined ? {} : { "Content-Type": "application/json" }),
      },
      ...(json === undefined ? {} : { body: JSON.stringify(json) }),
    });
  } catch {
    throw new Error("GITHUB_NETWORK_UNAVAILABLE");
  }
}

/**
 * @param {string} repository `owner/name`.
 * @param {string | undefined} token
 * @param {typeof fetch} [request]
 * @returns {GithubApi}
 */
export function githubApi(repository, token, request = fetch) {
  if (!/^[A-Za-z0-9][A-Za-z0-9-]*\/[A-Za-z0-9_.-]+$/u.test(repository) || !token)
    throw new Error("GITHUB_IDENTITY_REQUIRED");
  return async (path = "", { method = "GET", json } = {}) => {
    const response = await send(repository, token, request, path, { method, json });
    if (!response.ok)
      throw Object.assign(new Error(`GITHUB_HTTP_${response.status}`), { status: response.status });
    if (response.status === 204) return null;
    try {
      return await response.json();
    } catch {
      throw new Error("GITHUB_RESPONSE_INVALID");
    }
  };
}

const counted = { workflow_runs: 1000, jobs: 10000 };

/**
 * The total a counted listing declares, refusing an impossible or changing one.
 * @param {Json} document
 * @param {string} field
 * @param {number | undefined} previous
 * @returns {number}
 */
function declaredTotal(document, field, previous) {
  const count = document["total_count"];
  const ceiling = counted[/** @type {keyof typeof counted} */ (field)];
  if (
    !Number.isSafeInteger(count) ||
    count < 0 ||
    count > ceiling ||
    (previous !== undefined && count !== previous)
  )
    throw new Error("GITHUB_PAGINATION_INCOMPLETE");
  return count;
}

/**
 * @param {Json[]} rows
 * @param {Set<number>} ids Row ids seen so far; the rows' ids are added.
 */
function recordIds(rows, ids) {
  for (const row of rows) {
    if (!Number.isSafeInteger(row["id"]) || row["id"] <= 0 || ids.has(row["id"]))
      throw new Error("GITHUB_PAGINATION_DUPLICATE_OR_INVALID_ID");
    ids.add(row["id"]);
  }
}

/**
 * Every row of a paginated listing, refusing incomplete, duplicated or oversized pages.
 * @param {GithubApi} api
 * @param {string} path
 * @param {string} [field] The array field of a wrapped listing; absent for a bare array.
 * @returns {Promise<Json[]>}
 */
export async function pages(api, path, field) {
  /** @type {Json[]} */
  const values = [];
  const ids = new Set();
  /** @type {number | undefined} */
  let total;
  const isCounted = field !== undefined && Object.hasOwn(counted, field);
  for (let page = 1; page <= 100; page += 1) {
    const document = await api(`${path}${path.includes("?") ? "&" : "?"}per_page=100&page=${page}`);
    if (isCounted) total = declaredTotal(document, field, total);
    const rows = field === undefined ? document : /** @type {unknown} */ (document[field]);
    if (!Array.isArray(rows) || rows.length > 100) throw new Error("GITHUB_PAGINATION_INVALID");
    if (isCounted) recordIds(rows, ids);
    values.push(...rows);
    if (rows.length < 100) {
      if (isCounted && values.length !== total) throw new Error("GITHUB_PAGINATION_INCOMPLETE");
      return values;
    }
  }
  throw new Error("GITHUB_PAGINATION_INCOMPLETE");
}

/**
 * The tested merge commit of a pull request, from its merge ref, agreeing with any SHA the pull
 * request reports.
 * @param {GithubApi} api
 * @param {Json} pr
 * @returns {Promise<string>}
 */
export async function pullMergeRevision(api, pr) {
  const ref = `refs/pull/${pr["number"]}/merge`;
  const response = await api(`git/ref/pull/${pr["number"]}/merge`);
  const sha = response?.object?.sha;
  if (
    response?.ref !== ref ||
    response.object?.type !== "commit" ||
    typeof sha !== "string" ||
    !/^[0-9a-f]{40}$/u.test(sha) ||
    (pr["merge_commit_sha"] != null && pr["merge_commit_sha"] !== sha)
  )
    throw new Error("GITHUB_PR_MERGE_REVISION_MISMATCH");
  return sha;
}
