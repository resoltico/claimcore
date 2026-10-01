// Shapes shared by the governance and verification scripts. A parsed JSON or YAML document from an
// outside system (the GitHub API, workflow files, npm metadata) is data whose shape the policy code
// checks explicitly, so it is typed as an open record here and nowhere else.

/** @typedef {Record<string, any>} Json An outside document; policy code validates every field it reads. */

/**
 * @typedef {(path?: string, options?: { method?: string, json?: unknown }) => Promise<any>} GithubApi
 * A repository-scoped GitHub API client.
 */

/** @typedef {{ status?: number }} StatusError */

/**
 * @typedef {object} Stage
 * @property {string} id
 * @property {string[]} argv
 * @property {string[]} [after] Stages that must finish first.
 * @property {string} [group] Stages in one group never overlap.
 * @property {boolean} [exclusive] Runs alone.
 * @property {Record<string, string>} [env]
 * @property {string[]} [requires] Tools that must be on PATH.
 * @property {{ directories: string[], suffix: string }} [appendFiles]
 */

/**
 * @typedef {object} Plan
 * @property {string} producer
 * @property {Stage[]} stages
 */

/**
 * @typedef {{ status: "passed" } | { status: "failed", error?: unknown, note?: string } | { status: "skipped", note?: string } | { status: "not-started" }} StageResult
 */

export {};
