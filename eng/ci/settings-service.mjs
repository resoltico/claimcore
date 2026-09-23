import assert from "node:assert/strict";
import { pages } from "./github-api.mjs";
import { settingsPlan } from "./settings-policy.mjs";

export async function readSettings(api) {
  const repository = await api("");
  const listed = await pages(api, "rulesets?includes_parents=true");
  const rules = [];
  for (const rule of listed) {
    assert(
      rule.source_type === "Repository",
      "Inherited governance must be reconciled by its owner.",
    );
    rules.push(await api(`rulesets/${rule.id}`));
  }
  let environment;
  try {
    environment = await api("environments/release");
  } catch (error) {
    if (error.status !== 404) throw error;
    environment = null;
  }
  const branches = environment?.deployment_branch_policy?.custom_branch_policies
    ? await pages(
        api,
        "environments/release/deployment-branch-policies",
        "branch_policies",
      )
    : [];
  return { repository, rules, environment, branches };
}

export async function configureSettings(
  api,
  expectedPlanSha,
  progress = () => {},
) {
  const initial = settingsPlan(await readSettings(api));
  assert.equal(
    initial.planSha256,
    expectedPlanSha,
    "Settings changed since the reviewed plan; plan again.",
  );
  for (let index = 0; index < initial.operations.length; index += 1) {
    const remaining = initial.operations.slice(index);
    // Re-read and re-plan immediately before each write, preserving all unrelated/stronger policy.
    const current = settingsPlan(await readSettings(api));
    assert.equal(
      current.repositoryId,
      initial.repositoryId,
      "Repository identity changed.",
    );
    assert.equal(current.ownerId, initial.ownerId, "Owner identity changed.");
    assert.deepEqual(
      current.operations,
      remaining,
      "Concurrent configuration change; stopping without rollback.",
    );
    const operation = remaining[0];
    await api(operation.path, operation);
    const after = settingsPlan(await readSettings(api));
    assert.equal(
      after.repositoryId,
      initial.repositoryId,
      "Repository identity changed.",
    );
    assert.equal(after.ownerId, initial.ownerId, "Owner identity changed.");
    assert.deepEqual(
      after.operations,
      remaining.slice(1),
      "Configuration read-back differs; stop and inspect before retrying.",
    );
    progress({
      path: operation.path || "repository",
      method: operation.method,
      verified: true,
    });
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
