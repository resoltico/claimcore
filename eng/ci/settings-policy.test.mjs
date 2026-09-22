import test from "node:test";
import assert from "node:assert/strict";
import { settingsPlan } from "./settings-policy.mjs";
import { configureSettings } from "./settings-service.mjs";

function snapshot() {
  return {
    repository: {
      full_name: "owner/repo",
      id: 456,
      default_branch: "main",
      owner: { type: "User", id: 123 },
      delete_branch_on_merge: false,
      allow_update_branch: false,
    },
    rules: [
      {
        id: 7,
        source_type: "Repository",
        name: "Require verified ClaimCore Gate on main",
        target: "branch",
        enforcement: "active",
        bypass_actors: [],
        conditions: { ref_name: { include: ["refs/heads/main"], exclude: [] } },
        rules: [
          {
            type: "required_status_checks",
            parameters: {
              strict_required_status_checks_policy: true,
              do_not_enforce_on_create: false,
              required_status_checks: [
                { context: "Gate", integration_id: 15368 },
              ],
            },
          },
          { type: "non_fast_forward" },
          { type: "deletion" },
        ],
      },
    ],
    environment: null,
    branches: [],
  };
}
function fakeApi(state, hooks = {}) {
  let nextId = 8;
  return async (path, { method = "GET", json } = {}) => {
    if (method !== "GET") {
      hooks.write?.(path, method, json);
      if (path === "") Object.assign(state.repository, json);
      else if (path === "rulesets")
        state.rules.push({
          ...structuredClone(json),
          id: nextId++,
          source_type: "Repository",
        });
      else if (path.startsWith("rulesets/"))
        Object.assign(
          state.rules.find((rule) => rule.id === Number(path.split("/")[1])),
          structuredClone(json),
        );
      else if (path === "environments/release")
        state.environment = {
          deployment_branch_policy: json.deployment_branch_policy,
          protection_rules: [
            {
              type: "required_reviewers",
              prevent_self_review: json.prevent_self_review,
              reviewers: json.reviewers.map(({ type, id }) => ({
                type,
                reviewer: { id },
              })),
            },
            { type: "wait_timer", wait_timer: json.wait_timer },
          ],
        };
      else if (path === "environments/release/deployment-branch-policies")
        state.branches.push({ ...json, id: nextId++ });
      else throw new Error("Unexpected write.");
      return {};
    }
    hooks.read?.(path);
    if (path === "") return structuredClone(state.repository);
    if (path.startsWith("rulesets?")) return structuredClone(state.rules);
    if (path.startsWith("rulesets/"))
      return structuredClone(
        state.rules.find((rule) => rule.id === Number(path.split("/")[1])),
      );
    if (path.startsWith("environments/release/deployment-branch-policies?"))
      return { branch_policies: structuredClone(state.branches) };
    if (path === "environments/release" && state.environment)
      return structuredClone(state.environment);
    const error = new Error("absent");
    error.status = 404;
    throw error;
  };
}

test("minimal owner policy requires PRs without impossible self-approval", () => {
  const plan = settingsPlan(snapshot());
  const branch = plan.operations.find((op) => op.path === "rulesets/7");
  const pr = branch.json.rules.find(
    (rule) => rule.type === "pull_request",
  ).parameters;
  assert.equal(pr.required_approving_review_count, 0);
  assert.equal(pr.required_review_thread_resolution, true);
  assert.equal(
    branch.json.rules.find((rule) => rule.type === "required_status_checks")
      .parameters.strict_required_status_checks_policy,
    true,
  );
  assert.equal(
    plan.operations.find((op) => op.path === "environments/release").json
      .prevent_self_review,
    false,
  );
});
test("settings preserve stronger existing controls and unrelated required checks", () => {
  const state = snapshot();
  state.rules[0].rules.push(
    { type: "required_signatures" },
    {
      type: "pull_request",
      parameters: {
        required_approving_review_count: 2,
        require_last_push_approval: true,
        required_review_thread_resolution: false,
      },
    },
  );
  state.rules[0].rules[0].parameters.required_status_checks.push({
    context: "External review",
    integration_id: 777,
  });
  const rules = settingsPlan(state).operations.find(
    (op) => op.path === "rulesets/7",
  ).json.rules;
  assert(rules.some((rule) => rule.type === "required_signatures"));
  assert.equal(
    rules.find((rule) => rule.type === "pull_request").parameters
      .required_approving_review_count,
    2,
  );
  assert.equal(rules[0].parameters.required_status_checks.length, 2);
});
test("ambiguous provider and existing bypasses stop rather than silently rewriting authority", () => {
  const state = snapshot();
  state.rules[0].bypass_actors.push({ actor_type: "User", actor_id: 123 });
  assert.throws(() => settingsPlan(state));
  state.rules[0].bypass_actors = [];
  state.rules[0].rules[0].parameters.required_status_checks[0].integration_id = 999;
  assert.throws(() => settingsPlan(state));
});
test("configuration applies the reviewed plan and verifies idempotent read-back", async () => {
  const state = snapshot();
  const writes = [];
  const api = fakeApi(state, { write: (path) => writes.push(path) });
  const result = await configureSettings(api, settingsPlan(state).planSha256);
  assert.equal(result.outcome, "verified-scoped-policy");
  assert.equal(writes.length, 5);
  assert.equal(settingsPlan(state).operations.length, 0);
  assert.equal(
    (await configureSettings(api, settingsPlan(state).planSha256)).applied,
    0,
  );
  assert.equal(writes.length, 5);
});
test("a stale approved plan causes no configuration writes", async () => {
  const state = snapshot();
  const old = settingsPlan(state).planSha256;
  state.repository.allow_update_branch = true;
  let writes = 0;
  await assert.rejects(
    configureSettings(fakeApi(state, { write: () => writes++ }), old),
    /Settings changed/u,
  );
  assert.equal(writes, 0);
});
test("an administration denial does not invent absent configuration or retry writes", async () => {
  let calls = 0;
  await assert.rejects(
    configureSettings(async () => {
      calls++;
      const error = new Error("denied");
      error.status = 403;
      throw error;
    }, "a".repeat(64)),
  );
  assert.equal(calls, 1);
});
test("unknown release restrictions require owner reconciliation rather than deletion", () => {
  const state = snapshot();
  state.branches = [{ name: "production", type: "branch" }];
  assert.throws(() => settingsPlan(state), /explicit owner reconciliation/u);
});

test("approved configuration plans cannot be transplanted to a different repository", () => {
  const state = snapshot();
  const expected = settingsPlan(state).planSha256;
  state.repository.id = 457;
  state.repository.full_name = "owner/other";
  assert.notEqual(settingsPlan(state).planSha256, expected);
});
test("concurrent settings changes stop publication instead of overwriting stronger policy", async () => {
  const state = snapshot();
  const plan = settingsPlan(state).planSha256;
  let reads = 0,
    writes = 0;
  const api = fakeApi(state, {
    read(path) {
      if (path === "" && ++reads === 2)
        state.rules[0].bypass_actors.push({
          actor_type: "User",
          actor_id: 999,
        });
    },
    write() {
      writes++;
    },
  });
  await assert.rejects(configureSettings(api, plan));
  assert.equal(writes, 0);
});
