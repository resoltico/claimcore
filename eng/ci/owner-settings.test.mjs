import test from "node:test";
import assert from "node:assert/strict";
import { snapshot, fakeApi } from "./settings-fixture.mjs";
import { settingsPlan } from "./settings-policy.mjs";
import { ownerMergeRule } from "./owner-settings.mjs";
import { configureSettings } from "./settings-service.mjs";

test("owner restriction is separate from non-bypassable Gate and permits only PR merges", () => {
  const plan = settingsPlan(snapshot());
  const owner = plan.operations.find(
    (op) => op.json.name === ownerMergeRule(123).name,
  );
  assert.deepEqual(owner.json, ownerMergeRule(123));
  const gate = plan.operations.find((op) => op.path === "rulesets/7");
  assert.deepEqual(gate.json.bypass_actors, []);
  assert(
    !owner.json.rules.some((rule) => rule.type === "required_status_checks"),
  );
  assert.equal(
    plan.operations.find((op) => op.path === "").json.allow_auto_merge,
    false,
  );
});
for (const [label, mutate] of [
  [
    "always bypass",
    (r) => {
      r.bypass_actors[0].bypass_mode = "always";
    },
  ],
  [
    "different owner",
    (r) => {
      r.bypass_actors[0].actor_id = 999;
    },
  ],
  [
    "role-based bypass",
    (r) => {
      r.bypass_actors[0].actor_type = "RepositoryRole";
    },
  ],
  [
    "additional app",
    (r) => {
      r.bypass_actors.push({ actor_type: "Integration", actor_id: 1 });
    },
  ],
  [
    "missing bypass list",
    (r) => {
      delete r.bypass_actors;
    },
  ],
  [
    "broadened scope",
    (r) => {
      r.conditions.ref_name.include.push("refs/heads/*");
    },
  ],
  [
    "excluded main",
    (r) => {
      r.conditions.ref_name.exclude.push("refs/heads/main");
    },
  ],
  [
    "status rule mixed in",
    (r) => {
      r.rules.push({ type: "required_status_checks" });
    },
  ],
  [
    "fetch-and-merge bypass",
    (r) => {
      r.rules[0].parameters.update_allows_fetch_and_merge = true;
    },
  ],
])
  test(`owner settings refuse ${label} without narrowing unknown existing policy`, () => {
    const state = snapshot();
    const rule = { ...ownerMergeRule(123), id: 8 };
    mutate(rule);
    state.rules.push(rule);
    assert.throws(() => settingsPlan(state));
  });

test("a disabled exact owner rule is reactivated without replacing its authority", () => {
  const state = snapshot();
  state.rules.push({ ...ownerMergeRule(123), id: 8, enforcement: "disabled" });
  const op = settingsPlan(state).operations.find(
    (entry) => entry.path === "rulesets/8",
  );
  assert.deepEqual(op.json, ownerMergeRule(123));
});
for (const type of ["merge_queue", "update"])
  test(`conflicting ${type} needs explicit owner reconciliation`, () => {
    const state = snapshot();
    state.rules[0].rules.push({ type });
    assert.throws(() => settingsPlan(state), /owner reconciliation/u);
  });
test("missing owner identity is not converted into a broad admin exception", () => {
  const state = snapshot();
  state.repository.owner.id = 0;
  assert.throws(() => settingsPlan(state));
});
test("owner identity participates in plan binding even after policy converges", async () => {
  const state = snapshot();
  await configureSettings(fakeApi(state), settingsPlan(state).planSha256);
  const previous = settingsPlan(state).planSha256;
  state.repository.owner.id = 789;
  state.rules.find(
    (rule) => rule.name === ownerMergeRule(123).name,
  ).bypass_actors[0].actor_id = 789;
  assert.equal(settingsPlan(state).operations.length, 0);
  assert.notEqual(settingsPlan(state).planSha256, previous);
});
test("post-write owner changes abort without retrying or rolling back", async () => {
  const state = snapshot();
  let writes = 0;
  const api = fakeApi(state, {
    write() {
      writes++;
      state.repository.owner.id = 789;
    },
  });
  await assert.rejects(
    configureSettings(api, settingsPlan(state).planSha256),
    /Owner identity/u,
  );
  assert.equal(writes, 1);
});
