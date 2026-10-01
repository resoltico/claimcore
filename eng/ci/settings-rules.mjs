// The reviewed ruleset building blocks shared by the branch and tag operations.

/** @typedef {import("./types.mjs").Json} Json */
/** @typedef {{ path: string, method: string, json: Json }} Operation */

export const prRule = {
  type: "pull_request",
  parameters: {
    dismiss_stale_reviews_on_push: false,
    require_code_owner_review: false,
    require_last_push_approval: false,
    required_approving_review_count: 0,
    required_review_thread_resolution: true,
  },
};

export const gateIntegration = 15368;

export const gateRule = {
  type: "required_status_checks",
  parameters: {
    strict_required_status_checks_policy: true,
    do_not_enforce_on_create: false,
    required_status_checks: [{ context: "Gate", integration_id: gateIntegration }],
  },
};

/**
 * The writable fields of a ruleset.
 * @param {Json} rule
 * @returns {Json}
 */
export const ruleBody = (rule) =>
  Object.fromEntries(
    ["name", "target", "enforcement", "conditions", "bypass_actors", "rules"].map((key) => [
      key,
      structuredClone(rule[key]),
    ]),
  );

/**
 * Whether a ruleset includes exactly `expected` and excludes nothing.
 * @param {Json} value
 * @param {string} expected
 */
export const ref = (value, expected) =>
  Boolean(value?.conditions?.ref_name?.include?.includes(expected)) &&
  value.conditions.ref_name.exclude.length === 0;
