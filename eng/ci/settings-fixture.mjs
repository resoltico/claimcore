/** @typedef {import("./types.mjs").Json} Json */

/** @returns {Json} */
export function snapshot() {
  return {
    repository: {
      full_name: "owner/repo",
      id: 456,
      default_branch: "main",
      owner: { type: "User", id: 123 },
      delete_branch_on_merge: false,
      allow_update_branch: false,
    },
    rules: [
      {
        id: 7,
        source_type: "Repository",
        name: "Require verified ClaimCore Gate on main",
        target: "branch",
        enforcement: "active",
        bypass_actors: [],
        conditions: { ref_name: { include: ["refs/heads/main"], exclude: [] } },
        rules: [
          {
            type: "required_status_checks",
            parameters: {
              strict_required_status_checks_policy: true,
              do_not_enforce_on_create: false,
              required_status_checks: [{ context: "Gate", integration_id: 15368 }],
            },
          },
          { type: "non_fast_forward" },
          { type: "deletion" },
        ],
      },
    ],
    environment: null,
    branches: [],
  };
}

/** @param {Json} json */
const environmentFrom = (json) => ({
  deployment_branch_policy: json["deployment_branch_policy"],
  protection_rules: [
    {
      type: "required_reviewers",
      prevent_self_review: json["prevent_self_review"],
      reviewers: json["reviewers"].map((/** @type {Json} */ { type, id }) => ({
        type,
        reviewer: { id },
      })),
    },
    { type: "wait_timer", wait_timer: json["wait_timer"] },
  ],
});

/**
 * Record one write in the fake repository state.
 * @param {Json} state
 * @param {string} path
 * @param {Json} json
 * @param {() => number} nextId
 */
function applyWrite(state, path, json, nextId) {
  if (path === "") {
    Object.assign(state["repository"], json);
  } else if (path === "rulesets") {
    state["rules"].push({ ...structuredClone(json), id: nextId(), source_type: "Repository" });
  } else if (path.startsWith("rulesets/")) {
    Object.assign(
      state["rules"].find((/** @type {Json} */ rule) => rule["id"] === Number(path.split("/")[1])),
      structuredClone(json),
    );
  } else if (path === "environments/release") {
    state["environment"] = environmentFrom(json);
  } else if (path === "environments/release/deployment-branch-policies") {
    state["branches"].push({ ...json, id: nextId() });
  } else {
    throw new Error("Unexpected write.");
  }
}

/**
 * Answer one read from the fake repository state.
 * @param {Json} state
 * @param {string} path
 */
function answerRead(state, path) {
  if (path === "") {
    return structuredClone(state["repository"]);
  }
  if (path.startsWith("rulesets?")) {
    return structuredClone(state["rules"]);
  }
  if (path.startsWith("rulesets/")) {
    return structuredClone(
      state["rules"].find((/** @type {Json} */ rule) => rule["id"] === Number(path.split("/")[1])),
    );
  }
  if (path.startsWith("environments/release/deployment-branch-policies?")) {
    return { branch_policies: structuredClone(state["branches"]) };
  }
  if (path === "environments/release" && state["environment"]) {
    return structuredClone(state["environment"]);
  }
  throw Object.assign(new Error("absent"), { status: 404 });
}

/**
 * A fake repository API over `state`.
 * @param {Json} state
 * @param {{ write?: (path: string, method: string, json: unknown) => void, read?: (path: string) => void }} [hooks]
 * @returns {import("./types.mjs").GithubApi}
 */
export function fakeApi(state, hooks = {}) {
  let id = 8;
  const nextId = () => id++;
  return async (path = "", { method = "GET", json } = {}) => {
    if (method === "GET") {
      hooks.read?.(path);
      return answerRead(state, path);
    }
    hooks.write?.(path, method, json);
    applyWrite(state, path, /** @type {Json} */ (json), nextId);
    return {};
  };
}
