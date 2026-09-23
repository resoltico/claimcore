import assert from "node:assert/strict";
import { isDeepStrictEqual } from "node:util";

const name = "Owner-authorized ClaimCore PR merges";
export function ownerMergeRule(ownerId) {
  assert(
    Number.isSafeInteger(ownerId) && ownerId > 0,
    "Owner identity is required.",
  );
  return {
    name,
    target: "branch",
    enforcement: "active",
    conditions: { ref_name: { include: ["refs/heads/main"], exclude: [] } },
    bypass_actors: [
      { actor_type: "User", actor_id: ownerId, bypass_mode: "pull_request" },
    ],
    rules: [
      { type: "update", parameters: { update_allows_fetch_and_merge: false } },
    ],
  };
}

export function ownerMergeOperation(snapshot) {
  const desired = ownerMergeRule(snapshot.repository.owner.id);
  const candidates = snapshot.rules.filter((rule) => rule.name === name);
  assert(
    candidates.length <= 1,
    "Duplicate owner-merge rules require owner reconciliation.",
  );
  // Do not hide a conflicting update restriction behind a second nominally correct rule.
  assert(
    snapshot.rules.every(
      (rule) =>
        rule.name === name ||
        rule.target !== "branch" ||
        !rule.rules.some((item) =>
          ["update", "merge_queue"].includes(item.type),
        ),
    ),
    "Existing update or merge-queue policy requires explicit owner reconciliation.",
  );
  if (!candidates.length)
    return { path: "rulesets", method: "POST", json: desired };
  const current = candidates[0];
  assert(
    Number.isSafeInteger(current.id) && current.id > 0,
    "Owner ruleset identity is missing.",
  );
  for (const key of ["target", "conditions", "bypass_actors", "rules"])
    assert(
      isDeepStrictEqual(current[key], desired[key]),
      "Existing owner-merge scope or authority requires explicit owner reconciliation.",
    );
  assert(
    ["active", "disabled", "evaluate"].includes(current.enforcement),
    "Unknown enforcement.",
  );
  return current.enforcement === "active"
    ? null
    : {
        path: `rulesets/${current.id}`,
        method: "PUT",
        json: desired,
      };
}
