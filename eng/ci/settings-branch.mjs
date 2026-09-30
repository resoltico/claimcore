import assert from "node:assert/strict";
import { isDeepStrictEqual } from "node:util";
import { gateIntegration, gateRule, prRule, ref, ruleBody } from "./settings-rules.mjs";

/** @typedef {import("./settings-rules.mjs").Json} Json */
/** @typedef {import("./settings-rules.mjs").Operation} Operation */

const requiredRules = () => [{ type: "non_fast_forward" }, { type: "deletion" }, prRule];

/** @param {Json} rule */
const requiresGate = (rule) =>
  rule.rules.some(
    (/** @type {Json} */ item) =>
      item.type === "required_status_checks" &&
      item.parameters.required_status_checks.some(
        (/** @type {Json} */ check) => check.context === "Gate",
      ),
  );

/** @returns {Operation} */
function createGateRuleset() {
  return {
    path: "rulesets",
    method: "POST",
    json: {
      name: "Require verified ClaimCore Gate on main",
      target: "branch",
      enforcement: "active",
      conditions: { ref_name: { include: ["refs/heads/main"], exclude: [] } },
      bypass_actors: [],
      rules: [gateRule, ...requiredRules().slice(0, 2), prRule],
    },
  };
}

/**
 * The stricter ruleset that keeps everything already required and adds what the policy demands.
 * @param {Json} current
 * @returns {Json}
 */
function strengthened(current) {
  const desired = ruleBody(current);
  assert(
    Array.isArray(desired["bypass_actors"]) && desired["bypass_actors"].length === 0,
    "Existing bypass policy requires explicit owner reconciliation.",
  );
  desired["enforcement"] = "active";
  const checks = desired["rules"].find(
    (/** @type {Json} */ rule) => rule.type === "required_status_checks",
  );
  const gate = checks.parameters.required_status_checks.find(
    (/** @type {Json} */ check) => check.context === "Gate",
  );
  assert(
    gate.integration_id === gateIntegration,
    "Existing Gate provider differs from the reviewed Actions integration.",
  );
  checks.parameters.strict_required_status_checks_policy = true;
  checks.parameters.do_not_enforce_on_create = false;
  for (const rule of requiredRules())
    if (!desired["rules"].some((/** @type {Json} */ item) => item.type === rule.type))
      desired["rules"].push(structuredClone(rule));
  // Preserve stronger pre-existing approval requirements, unrelated checks and rules.
  desired["rules"].find(
    (/** @type {Json} */ rule) => rule.type === "pull_request",
  ).parameters.required_review_thread_resolution = true;
  return desired;
}

/**
 * The change that makes the `main` branch ruleset match policy, or null when it already does.
 * @param {Json} snapshot
 * @returns {Operation | null}
 */
export function branchOperation(snapshot) {
  const candidates = snapshot["rules"].filter(
    (/** @type {Json} */ rule) =>
      rule.target === "branch" &&
      ref(rule, "refs/heads/main") &&
      rule.conditions.ref_name.include.length === 1 &&
      requiresGate(rule),
  );
  assert(
    candidates.length <= 1,
    "Ambiguous main Gate rulesets require explicit owner reconciliation.",
  );
  const current = candidates[0];
  if (!current) return createGateRuleset();
  const desired = strengthened(current);
  return isDeepStrictEqual(ruleBody(current), desired)
    ? null
    : { path: `rulesets/${current.id}`, method: "PUT", json: desired };
}
