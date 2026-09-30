import assert from "node:assert/strict";

/** @typedef {import("./settings-rules.mjs").Json} Json */
/** @typedef {import("./settings-rules.mjs").Operation} Operation */

/**
 * The reviewers and timer an existing environment already has.
 * @param {Json[]} rules
 */
function existingProtection(rules) {
  assert(
    rules.every((rule) =>
      ["required_reviewers", "wait_timer", "branch_policy"].includes(rule.type),
    ),
    "Custom environment protections require explicit owner reconciliation.",
  );
  const reviewersRule = rules.find((rule) => rule.type === "required_reviewers");
  return {
    preventSelfReview: Boolean(reviewersRule?.prevent_self_review),
    reviewers: /** @type {{ type: string, id: number }[]} */ (
      (reviewersRule?.reviewers ?? []).map((/** @type {Json} */ item) => ({
        type: item.type,
        id: item.reviewer.id,
      }))
    ),
    waitTimer: Number(rules.find((rule) => rule.type === "wait_timer")?.wait_timer ?? 0),
  };
}

/**
 * The changes that make the release environment require reviewers and deploy only from `main`.
 * @param {Json} snapshot
 * @returns {Operation[]}
 */
export function environmentOperations(snapshot) {
  const current = snapshot["environment"];
  const { preventSelfReview, reviewers, waitTimer } = existingProtection(
    current?.protection_rules ?? [],
  );
  assert(
    snapshot["branches"].every(
      (/** @type {Json} */ branch) => branch.name === "main" && branch.type === "branch",
    ),
    "Existing release deployment branches require explicit owner reconciliation.",
  );
  /** @type {Operation[]} */
  const changes = [];
  if (
    !current ||
    reviewers.length === 0 ||
    current.deployment_branch_policy?.custom_branch_policies !== true
  ) {
    changes.push({
      path: "environments/release",
      method: "PUT",
      json: {
        wait_timer: waitTimer,
        prevent_self_review: preventSelfReview,
        reviewers:
          reviewers.length > 0
            ? reviewers
            : [{ type: "User", id: snapshot["repository"].owner.id }],
        deployment_branch_policy: { protected_branches: false, custom_branch_policies: true },
      },
    });
  }
  if (snapshot["branches"].length === 0) {
    changes.push({
      path: "environments/release/deployment-branch-policies",
      method: "POST",
      json: { name: "main", type: "branch" },
    });
  }
  return changes;
}
