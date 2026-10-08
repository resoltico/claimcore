/** @typedef {import("./types.mjs").Json} Json */
import test from "node:test";
import assert from "node:assert/strict";
import { snapshot, fakeApi } from "./settings-fixture.mjs";
import { settingsPlan } from "./settings-policy.mjs";
import { configureSettings } from "./settings-service.mjs";
import { ownerMergeOperation, ownerMergeRule } from "./owner-settings.mjs";
import { tagOperation } from "./settings-tag.mjs";

test("settings writes converge when GitHub reads back parameter-free update rules", async () => {
  const state = snapshot();
  const storage = fakeApi(state);
  let readbacks = 0;
  /** @type {import("./types.mjs").GithubApi} */
  const api = async (path, options) => {
    const result = await storage(path, options);
    if ((!options || options.method === "GET") && path?.startsWith("rulesets/")) {
      result["rules"] = result["rules"].map((/** @type {Json} */ rule) => {
        if (rule.type !== "update") {
          return rule;
        }
        assert.deepEqual(rule.parameters, { update_allows_fetch_and_merge: false });
        readbacks++;
        return { type: "update" };
      });
    }
    return result;
  };
  const result = await configureSettings(api, settingsPlan(state).planSha256);
  assert.equal(result.outcome, "verified-scoped-policy");
  assert(readbacks > 0);
  const owner = state["rules"].find(
    (/** @type {Json} */ rule) => rule.name === ownerMergeRule(123).name,
  );
  assert.deepEqual(owner.bypass_actors, [
    { actor_type: "User", actor_id: 123, bypass_mode: "pull_request" },
  ]);
  assert.deepEqual(state["rules"][0].bypass_actors, []);
});

/** @param {Json} update */
function stateWithUpdate(update) {
  const state = snapshot();
  state["rules"].push({ ...ownerMergeRule(123), id: 8, rules: [update] });
  state["rules"].push({
    id: 9,
    name: "Immutable ClaimCore version tags",
    target: "tag",
    enforcement: "active",
    bypass_actors: [],
    conditions: { ref_name: { include: ["refs/tags/v*"], exclude: [] } },
    rules: [update, { type: "deletion" }],
  });
  return state;
}

test("exact parameter-free update rules need no owner or tag rewrite", () => {
  const state = stateWithUpdate({ type: "update" });
  assert.equal(ownerMergeOperation(state), null);
  assert.equal(tagOperation(state), null);
});

for (const parameters of [
  null,
  {},
  { update_allows_fetch_and_merge: true },
  { update_allows_fetch_and_merge: "false" },
  { update_allows_fetch_and_merge: false, unknown: false },
]) {
  test(`owner and tag readback refuse update parameters ${JSON.stringify(parameters)}`, () => {
    const state = stateWithUpdate({ type: "update", parameters });
    assert.throws(() => ownerMergeOperation(state), /owner reconciliation/u);
    assert.throws(() => tagOperation(state), /owner reconciliation/u);
  });
}

test("parameter-free update readback does not hide unfamiliar rule fields", () => {
  const state = stateWithUpdate({ type: "update", unknown: false });
  assert.throws(() => ownerMergeOperation(state), /owner reconciliation/u);
  assert.throws(() => tagOperation(state), /owner reconciliation/u);
});
