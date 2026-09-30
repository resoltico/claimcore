import assert from "node:assert/strict";
import { ref } from "./settings-rules.mjs";

/** @typedef {import("./settings-rules.mjs").Json} Json */
/** @typedef {import("./settings-rules.mjs").Operation} Operation */

/**
 * The change that makes version tags immutable, or null when they already are.
 * @param {Json} snapshot
 * @returns {Operation | null}
 */
export function tagOperation(snapshot) {
  const matching = snapshot["rules"].filter(
    (/** @type {Json} */ rule) => rule.target === "tag" && ref(rule, "refs/tags/v*"),
  );
  const protectedTags = matching.some(
    (/** @type {Json} */ rule) =>
      rule.enforcement === "active" &&
      rule.bypass_actors.length === 0 &&
      ["update", "deletion"].every((type) =>
        rule.rules.some((/** @type {Json} */ item) => item.type === type),
      ),
  );
  if (protectedTags) return null;
  assert(
    !snapshot["rules"].some(
      (/** @type {Json} */ rule) => rule.name === "Immutable ClaimCore version tags",
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
        { type: "update", parameters: { update_allows_fetch_and_merge: false } },
        { type: "deletion" },
      ],
    },
  };
}
