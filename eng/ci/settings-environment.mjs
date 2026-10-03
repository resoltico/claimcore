import assert from "node:assert/strict";

/** @typedef {import("./settings-rules.mjs").Json} Json */
/** @typedef {import("./settings-rules.mjs").Operation} Operation */

/** @param {Json | undefined} reviewersRule */
function existingReviewers(reviewersRule) {
  if (reviewersRule) {
    assert(
      typeof reviewersRule.prevent_self_review === "boolean" &&
        Array.isArray(reviewersRule.reviewers) &&
        reviewersRule.reviewers.length <= 6,
      "Existing reviewer protection is malformed; reconcile with the owner.",
    );
  }
  const reviewers = /** @type {{ type: string, id: number }[]} */ (
    (reviewersRule?.reviewers ?? []).map((/** @type {Json} */ item) => ({
      type: item?.type,
      id: item?.reviewer?.id,
    }))
  );
  assert(
    reviewers.every(
      ({ type, id }) => ["User", "Team"].includes(type) && Number.isSafeInteger(id) && id > 0,
    ) && new Set(reviewers.map(({ type, id }) => `${type}:${id}`)).size === reviewers.length,
    "Existing reviewer identities are invalid or duplicated; reconcile with the owner.",
  );
  return { preventSelfReview: reviewersRule?.prevent_self_review ?? false, reviewers };
}

/**
 * The reviewers and timer an existing environment already has.
 * @param {Json[]} rules
 */
function existingProtection(rules) {
  assert(
    Array.isArray(rules),
    "Release protection rules are unavailable; reconcile with the owner.",
  );
  const types = rules.map((rule) => rule?.type);
  assert(
    types.every((type) => ["required_reviewers", "wait_timer", "branch_policy"].includes(type)) &&
      new Set(types).size === types.length,
    "Unknown or duplicate environment protections require explicit owner reconciliation.",
  );
  const { preventSelfReview, reviewers } = existingReviewers(
    rules.find((rule) => rule.type === "required_reviewers"),
  );
  const timerRule = rules.find((rule) => rule.type === "wait_timer");
  const waitTimer = timerRule ? timerRule.wait_timer : 0;
  assert(
    Number.isSafeInteger(waitTimer) && waitTimer >= 0 && waitTimer <= 43200,
    "Existing wait timer is invalid; reconcile with the owner.",
  );
  return {
    preventSelfReview,
    reviewers,
    waitTimer,
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
    current === null ? [] : current?.protection_rules,
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
