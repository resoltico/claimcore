import assert from "node:assert/strict";
import test from "node:test";
import { runPlan, validatePlan } from "./stage-plan.mjs";

/** @param {Array<Partial<import("./types.mjs").Stage> & { id: string }>} stages */
const plan = (stages) => ({
  producer: "test",
  stages: stages.map((stage) => ({ argv: ["true"], ...stage })),
});
/** @param {number} milliseconds */
const pause = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

test("a plan must name unique, known, acyclic stages", () => {
  assert.throws(() => validatePlan(plan([{ id: "one" }, { id: "one" }])), /twice/);
  assert.throws(() => validatePlan(plan([{ id: "one", after: ["missing"] }])), /unknown/);
  assert.throws(() => validatePlan(plan([{ id: "one", after: ["one"] }])), /itself/);
  assert.throws(
    () =>
      validatePlan(
        plan([
          { id: "one", after: ["two"] },
          { id: "two", after: ["one"] },
        ]),
      ),
    /cycle/,
  );
  assert.throws(() => validatePlan(plan([{ id: "Bad Id" }])), /kebab/);
  assert.throws(
    () => validatePlan({ producer: "test", stages: [{ id: "one", argv: [] }] }),
    /command/,
  );
  validatePlan(plan([{ id: "one" }, { id: "two", after: ["one"] }]));
});

test("independent stages overlap up to the limit and no further", async () => {
  let active = 0;
  let peak = 0;
  const results = await runPlan(
    plan(["a", "b", "c", "d", "e"].map((id) => ({ id }))),
    3,
    async () => {
      active += 1;
      peak = Math.max(peak, active);
      await pause(20);
      active -= 1;
      return { failed: false };
    },
  );
  assert.equal(results.length, 5);
  assert.equal(peak, 3);
});

test("a stage never starts before the stages it follows have finished", async () => {
  /** @type {string[]} */
  const order = [];
  await runPlan(plan([{ id: "late", after: ["early"] }, { id: "early" }]), 4, async (stage) => {
    order.push(`start ${stage.id}`);
    await pause(stage.id === "early" ? 30 : 1);
    order.push(`end ${stage.id}`);
    return { failed: false };
  });
  assert.deepEqual(order, ["start early", "end early", "start late", "end late"]);
});

test("stages sharing a group never overlap while others do", async () => {
  const active = new Set();
  let overlapped = false;
  let free = 0;
  await runPlan(
    plan([
      { id: "one", group: "docker" },
      { id: "two", group: "docker" },
      { id: "three", group: "docker" },
      { id: "solo" },
    ]),
    4,
    async (stage) => {
      if (stage.group === "docker") {
        overlapped ||= [...active].some((id) => id !== stage.id);
        active.add(stage.id);
      } else free += active.size;
      await pause(15);
      active.delete(stage.id);
      return { failed: false };
    },
  );
  assert.equal(overlapped, false);
  assert.ok(free >= 1, "the ungrouped stage ran alongside a grouped one");
});

test("an exclusive stage runs alone and nothing starts while it runs", async () => {
  const active = new Set();
  let exclusiveSawOthers = false;
  let othersSawExclusive = false;
  await runPlan(
    plan([{ id: "a" }, { id: "b" }, { id: "solo", exclusive: true }, { id: "c" }, { id: "d" }]),
    4,
    async (stage) => {
      if (stage.id === "solo") exclusiveSawOthers = active.size > 0;
      else othersSawExclusive ||= active.has("solo");
      active.add(stage.id);
      await pause(15);
      active.delete(stage.id);
      return { failed: false };
    },
  );
  assert.equal(exclusiveSawOthers, false);
  assert.equal(othersSawExclusive, false);
});

test("a failing or throwing stage is reported and does not stop the others", async () => {
  const results = await runPlan(
    plan([{ id: "bad" }, { id: "thrower" }, { id: "good" }]),
    2,
    async (stage) => {
      if (stage.id === "thrower") throw new Error("boom");
      return { failed: stage.id === "bad" };
    },
  );
  const failed = results
    .filter((result) => result.value.failed)
    .map((result) => result.stage.id)
    .sort();
  assert.deepEqual(failed, ["bad", "thrower"]);
  assert.equal(results.length, 3);
});

test("with fail-fast nothing starts after a failure and the rest are reported as not started", async () => {
  /** @type {string[]} */
  const started = [];
  const results = await runPlan(
    plan([{ id: "first" }, { id: "second", after: ["first"] }, { id: "third", after: ["second"] }]),
    2,
    async (stage) => {
      started.push(stage.id);
      return { failed: stage.id === "first" };
    },
    { failFast: true },
  );
  assert.deepEqual(started, ["first"]);
  const notStarted = results
    .filter((result) => result.value.notStarted)
    .map((result) => result.stage.id)
    .sort();
  assert.deepEqual(notStarted, ["second", "third"]);
});

test("without fail-fast every stage still runs after a failure", async () => {
  /** @type {string[]} */
  const started = [];
  await runPlan(plan([{ id: "first" }, { id: "second", after: ["first"] }]), 2, async (stage) => {
    started.push(stage.id);
    return { failed: stage.id === "first" };
  });
  assert.deepEqual(started, ["first", "second"]);
});

test("concurrency must be a positive integer", async () => {
  await assert.rejects(
    runPlan(plan([{ id: "one" }]), 0, async () => ({})),
    /positive/,
  );
});
