import assert from "node:assert/strict";
import { isDeepStrictEqual } from "node:util";
import { createHash } from "node:crypto";

const prRule = {
  type: "pull_request",
  parameters: {
    dismiss_stale_reviews_on_push: false,
    require_code_owner_review: false,
    require_last_push_approval: false,
    required_approving_review_count: 0,
    required_review_thread_resolution: true,
  },
};
const gateRule = {
  type: "required_status_checks",
  parameters: {
    strict_required_status_checks_policy: true,
    do_not_enforce_on_create: false,
    required_status_checks: [{ context: "Gate", integration_id: 15368 }],
  },
};
const ruleBody = (rule) =>
  Object.fromEntries(
    [
      "name",
      "target",
      "enforcement",
      "conditions",
      "bypass_actors",
      "rules",
    ].map((key) => [key, structuredClone(rule[key])]),
  );
const ref = (value, expected) =>
  value?.conditions?.ref_name?.include?.includes(expected) &&
  value.conditions.ref_name.exclude.length === 0;

function branchOperation(snapshot) {
  const candidates = snapshot.rules.filter(
    (rule) =>
      rule.target === "branch" &&
      ref(rule, "refs/heads/main") &&
      rule.conditions.ref_name.include.length === 1 &&
      rule.rules.some(
        (item) =>
          item.type === "required_status_checks" &&
          item.parameters.required_status_checks.some(
            (check) => check.context === "Gate",
          ),
      ),
  );
  assert(
    candidates.length <= 1,
    "Ambiguous main Gate rulesets require explicit owner reconciliation.",
  );
  if (!candidates.length)
    return {
      path: "rulesets",
      method: "POST",
      json: {
        name: "Require verified ClaimCore Gate on main",
        target: "branch",
        enforcement: "active",
        conditions: { ref_name: { include: ["refs/heads/main"], exclude: [] } },
        bypass_actors: [],
        rules: [
          gateRule,
          { type: "non_fast_forward" },
          { type: "deletion" },
          prRule,
        ],
      },
    };
  const current = candidates[0];
  const desired = ruleBody(current);
  assert(
    Array.isArray(desired.bypass_actors) && desired.bypass_actors.length === 0,
    "Existing bypass policy requires explicit owner reconciliation.",
  );
  desired.enforcement = "active";
  const checks = desired.rules.find(
    (rule) => rule.type === "required_status_checks",
  );
  const gate = checks.parameters.required_status_checks.find(
    (check) => check.context === "Gate",
  );
  assert(
    gate.integration_id === 15368,
    "Existing Gate provider differs from the reviewed Actions integration.",
  );
  checks.parameters.strict_required_status_checks_policy = true;
  checks.parameters.do_not_enforce_on_create = false;
  for (const rule of [
    { type: "non_fast_forward" },
    { type: "deletion" },
    prRule,
  ]) {
    if (!desired.rules.some((item) => item.type === rule.type))
      desired.rules.push(structuredClone(rule));
  }
  // Preserve stronger pre-existing approval requirements, unrelated checks and rules.
  desired.rules.find(
    (rule) => rule.type === "pull_request",
  ).parameters.required_review_thread_resolution = true;
  return isDeepStrictEqual(ruleBody(current), desired)
    ? null
    : { path: `rulesets/${current.id}`, method: "PUT", json: desired };
}

function tagOperation(snapshot) {
  const matching = snapshot.rules.filter(
    (rule) => rule.target === "tag" && ref(rule, "refs/tags/v*"),
  );
  if (
    matching.some(
      (rule) =>
        rule.enforcement === "active" &&
        rule.bypass_actors.length === 0 &&
        ["update", "deletion"].every((type) =>
          rule.rules.some((item) => item.type === type),
        ),
    )
  )
    return null;
  assert(
    !snapshot.rules.some(
      (rule) => rule.name === "Immutable ClaimCore version tags",
    ),
    "Existing tag policy requires explicit owner reconciliation.",
  );
  return {
    path: "rulesets",
    method: "POST",
    json: {
      name: "Immutable ClaimCore version tags",
      target: "tag",
      enforcement: "active",
      bypass_actors: [],
      conditions: { ref_name: { include: ["refs/tags/v*"], exclude: [] } },
      rules: [
        {
          type: "update",
          parameters: { update_allows_fetch_and_merge: false },
        },
        { type: "deletion" },
      ],
    },
  };
}

function environmentOperations(snapshot) {
  const current = snapshot.environment;
  const rules = current?.protection_rules ?? [];
  assert(
    rules.every((rule) =>
      ["required_reviewers", "wait_timer", "branch_policy"].includes(rule.type),
    ),
    "Custom environment protections require explicit owner reconciliation.",
  );
  const reviewersRule = rules.find(
    (rule) => rule.type === "required_reviewers",
  );
  const reviewers =
    reviewersRule?.reviewers.map((item) => ({
      type: item.type,
      id: item.reviewer.id,
    })) ?? [];
  const waitTimer =
    rules.find((rule) => rule.type === "wait_timer")?.wait_timer ?? 0;
  assert(
    snapshot.branches.every(
      (branch) => branch.name === "main" && branch.type === "branch",
    ),
    "Existing release deployment branches require explicit owner reconciliation.",
  );
  const changes = [];
  if (
    !current ||
    !reviewers.length ||
    current.deployment_branch_policy?.custom_branch_policies !== true
  ) {
    changes.push({
      path: "environments/release",
      method: "PUT",
      json: {
        wait_timer: waitTimer,
        prevent_self_review: reviewersRule?.prevent_self_review ?? false,
        reviewers: reviewers.length
          ? reviewers
          : [{ type: "User", id: snapshot.repository.owner.id }],
        deployment_branch_policy: {
          protected_branches: false,
          custom_branch_policies: true,
        },
      },
    });
  }
  if (!snapshot.branches.length)
    changes.push({
      path: "environments/release/deployment-branch-policies",
      method: "POST",
      json: { name: "main", type: "branch" },
    });
  return changes;
}

export function settingsPlan(snapshot) {
  assert(
    typeof snapshot.repository.full_name === "string" &&
      Number.isSafeInteger(snapshot.repository.id),
    "A plan must bind its repository identity.",
  );
  assert(
    snapshot.repository.default_branch === "main",
    "The reviewed repository default branch must be main.",
  );
  assert(
    snapshot.repository.owner.type === "User" &&
      Number.isSafeInteger(snapshot.repository.owner.id),
    "This reviewed sole-owner policy requires a personal repository.",
  );
  const operations = [];
  const flags = {};
  if (!snapshot.repository.delete_branch_on_merge)
    flags.delete_branch_on_merge = true;
  if (!snapshot.repository.allow_update_branch)
    flags.allow_update_branch = true;
  if (Object.keys(flags).length)
    operations.push({ path: "", method: "PATCH", json: flags });
  for (const operation of [
    branchOperation(snapshot),
    tagOperation(snapshot),
    ...environmentOperations(snapshot),
  ])
    if (operation) operations.push(operation);
  return {
    repository: snapshot.repository.full_name,
    operations,
    planSha256: createHash("sha256")
      .update(
        JSON.stringify({
          repository: snapshot.repository.full_name,
          id: snapshot.repository.id,
          operations,
        }),
      )
      .digest("hex"),
    authorization:
      "explicit-owner-merge-and-publication-review; not independent self-review",
  };
}
