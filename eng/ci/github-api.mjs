export function githubApi(repository, token, request = fetch) {
  if (
    !/^[A-Za-z0-9][A-Za-z0-9-]*\/[A-Za-z0-9_.-]+$/u.test(repository) ||
    !token
  )
    throw new Error("GITHUB_IDENTITY_REQUIRED");
  return async (path = "", { method = "GET", json } = {}) => {
    if (path.startsWith("/") || path.includes("..") || path.includes("://"))
      throw new Error("GITHUB_PATH_REFUSED");
    let response;
    try {
      response = await request(
        `https://api.github.com/repos/${repository}${path ? `/${path}` : ""}`,
        {
          method,
          redirect: "error",
          signal: AbortSignal.timeout(30000),
          headers: {
            Accept: "application/vnd.github+json",
            Authorization: `Bearer ${token}`,
            "X-GitHub-Api-Version": "2026-03-10",
            "User-Agent": "claimcore-governance",
            ...(json === undefined
              ? {}
              : { "Content-Type": "application/json" }),
          },
          body: json === undefined ? undefined : JSON.stringify(json),
        },
      );
    } catch {
      throw new Error("GITHUB_NETWORK_UNAVAILABLE");
    }
    if (!response.ok) {
      const error = new Error(`GITHUB_HTTP_${response.status}`);
      error.status = response.status;
      throw error;
    }
    if (response.status === 204) return null;
    try {
      return await response.json();
    } catch {
      throw new Error("GITHUB_RESPONSE_INVALID");
    }
  };
}
export async function pages(api, path, field) {
  const values = [];
  const ids = new Set();
  let total;
  const counted = ["workflow_runs", "jobs"].includes(field);
  for (let page = 1; page <= 100; page += 1) {
    const document = await api(
      `${path}${path.includes("?") ? "&" : "?"}per_page=100&page=${page}`,
    );
    if (counted) {
      const count = document.total_count;
      if (
        !Number.isSafeInteger(count) ||
        count < 0 ||
        count > (field === "workflow_runs" ? 1000 : 10000) ||
        (total !== undefined && count !== total)
      )
        throw new Error("GITHUB_PAGINATION_INCOMPLETE");
      total = count;
    }
    const rows = field === undefined ? document : document[field];
    if (!Array.isArray(rows) || rows.length > 100)
      throw new Error("GITHUB_PAGINATION_INVALID");
    if (counted)
      for (const row of rows) {
        if (!Number.isSafeInteger(row.id) || row.id <= 0 || ids.has(row.id))
          throw new Error("GITHUB_PAGINATION_DUPLICATE_OR_INVALID_ID");
        ids.add(row.id);
      }
    values.push(...rows);
    if (rows.length < 100) {
      if (counted && values.length !== total)
        throw new Error("GITHUB_PAGINATION_INCOMPLETE");
      return values;
    }
  }
  throw new Error("GITHUB_PAGINATION_INCOMPLETE");
}

export async function pullMergeRevision(api, pr) {
  const ref = `refs/pull/${pr.number}/merge`;
  const response = await api(`git/ref/pull/${pr.number}/merge`);
  const sha = response?.object?.sha;
  if (
    response?.ref !== ref ||
    response.object?.type !== "commit" ||
    typeof sha !== "string" ||
    !/^[0-9a-f]{40}$/u.test(sha) ||
    (pr.merge_commit_sha != null && pr.merge_commit_sha !== sha)
  )
    throw new Error("GITHUB_PR_MERGE_REVISION_MISMATCH");
  return sha;
}
