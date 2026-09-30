import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { ownerMergeOperation } from "./owner-settings.mjs";
import { branchOperation } from "./settings-branch.mjs";
import { environmentOperations } from "./settings-environment.mjs";
import { tagOperation } from "./settings-tag.mjs";

/** @typedef {import("./settings-rules.mjs").Json} Json */
/** @typedef {import("./settings-rules.mjs").Operation} Operation */

/**
 * Refuse a snapshot whose repository is not the reviewed personal repository.
 * @param {Json} repository
 */
function assertReviewedRepository(repository) {
  assert(
    typeof repository.full_name === "string" && Number.isSafeInteger(repository.id),
    "A plan must bind its repository identity.",
  );
  assert(
    repository.default_branch === "main",
    "The reviewed repository default branch must be main.",
  );
  assert(
    repository.owner.type === "User" &&
      Number.isSafeInteger(repository.owner.id) &&
      repository.owner.id > 0,
    "This reviewed sole-owner policy requires a personal repository.",
  );
}

/**
 * The repository flags that differ from policy.
 * @param {Json} repository
 * @returns {Operation[]}
 */
function flagOperations(repository) {
  /** @type {Record<string, boolean>} */
  const flags = {};
  if (repository.allow_auto_merge !== false) flags["allow_auto_merge"] = false;
  if (!repository.delete_branch_on_merge) flags["delete_branch_on_merge"] = true;
  if (!repository.allow_update_branch) flags["allow_update_branch"] = true;
  return Object.keys(flags).length > 0 ? [{ path: "", method: "PATCH", json: flags }] : [];
}

/**
 * The reviewed set of changes that bring a repository snapshot to policy, bound to its identity.
 * @param {Json} snapshot
 */
export function settingsPlan(snapshot) {
  const { repository } = snapshot;
  assertReviewedRepository(repository);
  const operations = [
    ...flagOperations(repository),
    ...[branchOperation(snapshot), ownerMergeOperation(snapshot), tagOperation(snapshot)].filter(
      (operation) => operation !== null,
    ),
    ...environmentOperations(snapshot),
  ];
  const identity = {
    repository: repository.full_name,
    id: repository.id,
    ownerId: repository.owner.id,
    operations,
  };
  return {
    repository: repository.full_name,
    repositoryId: repository.id,
    ownerId: repository.owner.id,
    operations,
    planSha256: createHash("sha256").update(JSON.stringify(identity)).digest("hex"),
    authorization:
      "owner-only-PR-merge; separate-non-bypassable-Gate; not independent human identity",
  };
}
