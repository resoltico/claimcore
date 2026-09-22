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
  for (let page = 1; page <= 100; page += 1) {
    const document = await api(
      `${path}${path.includes("?") ? "&" : "?"}per_page=100&page=${page}`,
    );
    if (
      field === "workflow_runs" &&
      (!Number.isSafeInteger(document.total_count) ||
        document.total_count > 1000)
    )
      throw new Error("GITHUB_PAGINATION_INCOMPLETE");
    const rows = field === undefined ? document : document[field];
    if (!Array.isArray(rows)) throw new Error("GITHUB_PAGINATION_INVALID");
    values.push(...rows);
    if (rows.length < 100) return values;
  }
  throw new Error("GITHUB_PAGINATION_INCOMPLETE");
}
