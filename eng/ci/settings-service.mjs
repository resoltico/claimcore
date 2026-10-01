import assert from "node:assert/strict";
import { pages } from "./github-api.mjs";
import { settingsPlan } from "./settings-policy.mjs";

/** @typedef {import("./types.mjs").Json} Json */

/**
 * Everything the settings policy reads from the repository.
 * @param {import("./types.mjs").GithubApi} api
 * @returns {Promise<Json>}
 */
export async function readSettings(api) {
  const repository = await api("");
  const listed = await pages(api, "rulesets?includes_parents=true");
  /** @type {Json[]} */
  const rules = [];
  for (const rule of listed) {
    assert(
      rule["source_type"] === "Repository",
      "Inherited governance must be reconciled by its owner.",
    );
    rules.push(await api(`rulesets/${rule["id"]}`));
  }
  /** @type {Json | null} */
  let environment;
  try {
    environment = await api("environments/release");
  } catch (error) {
    if (/** @type {import("./types.mjs").StatusError} */ (error).status !== 404) {
      throw error;
    }
    environment = null;
  }
  const branches = environment?.["deployment_branch_policy"]?.custom_branch_policies
    ? await pages(api, "environments/release/deployment-branch-policies", "branch_policies")
    : [];
  return { repository, rules, environment, branches };
}

/**
 * @param {ReturnType<typeof settingsPlan>} plan
 * @param {ReturnType<typeof settingsPlan>} initial
 */
function assertSameIdentity(plan, initial) {
  assert.equal(plan.repositoryId, initial.repositoryId, "Repository identity changed.");
  assert.equal(plan.ownerId, initial.ownerId, "Owner identity changed.");
}

/**
 * Write one operation after re-planning, then verify the read-back.
 * @param {import("./types.mjs").GithubApi} api
 * @param {ReturnType<typeof settingsPlan>} initial
 * @param {import("./settings-rules.mjs").Operation[]} remaining
 */
async function applyOne(api, initial, remaining) {
  // Re-read and re-plan immediately before each write, preserving all unrelated/stronger policy.
  const current = settingsPlan(await readSettings(api));
  assertSameIdentity(current, initial);
  assert.deepEqual(
    current.operations,
    remaining,
    "Concurrent configuration change; stopping without rollback.",
  );
  const [operation] = /** @type {[import("./settings-rules.mjs").Operation]} */ (remaining);
  await api(operation.path, operation);
  const after = settingsPlan(await readSettings(api));
  assertSameIdentity(after, initial);
  assert.deepEqual(
    after.operations,
    remaining.slice(1),
    "Configuration read-back differs; stop and inspect before retrying.",
  );
  return operation;
}

/**
 * Apply the reviewed plan one verified operation at a time.
 * @param {import("./types.mjs").GithubApi} api
 * @param {string} expectedPlanSha
 * @param {(entry: { path: string, method: string, verified: boolean }) => void} [progress]
 */
export async function configureSettings(
  api,
  expectedPlanSha,
  progress = () => {
    /* empty */
  },
) {
  const initial = settingsPlan(await readSettings(api));
  assert.equal(
    initial.planSha256,
    expectedPlanSha,
    "Settings changed since the reviewed plan; plan again.",
  );
  for (let index = 0; index < initial.operations.length; index += 1) {
    const operation = await applyOne(api, initial, initial.operations.slice(index));
    progress({ path: operation.path || "repository", method: operation.method, verified: true });
  }
  const final = settingsPlan(await readSettings(api));
  assert.equal(final.operations.length, 0, "Configuration did not converge.");
  return {
    outcome: "verified-scoped-policy",
    scope: [
      "repository-flags",
      "main-Gate-ruleset",
      "owner-only-PR-update-ruleset",
      "manual-merge-only",
      "version-tag-immutability",
      "release-environment",
    ],
    notAssessed: [
      "classic-branch-protection",
      "Actions-execution-policy",
      "credential-scopes",
      "independent-identity-separation",
    ],
    applied: initial.operations.length,
    authorization: final.authorization,
  };
}
