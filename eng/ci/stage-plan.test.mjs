import assert from "node:assert/strict";
import test from "node:test";
import { runPlan, validatePlan } from "./stage-plan.mjs";

/** @param {Array<Partial<import("./types.mjs").Stage> & { id: string }>} stages */
const plan = (stages) => ({
  producer: "test",
  stages: stages.map((stage) => ({ argv: ["true"], ...stage })),
});
/** @param {number} milliseconds */
const pause = (milliseconds) =>
  new Promise((resolve) => {
    setTimeout(resolve, milliseconds);
  });

test("a plan must name unique, known, acyclic stages", () => {
  assert.throws(() => validatePlan(plan([{ id: "one" }, { id: "one" }])), /twice/u);
  assert.throws(() => validatePlan(plan([{ id: "one", after: ["missing"] }])), /unknown/u);
  assert.throws(() => validatePlan(plan([{ id: "one", after: ["one"] }])), /itself/u);
  assert.throws(
    () =>
      validatePlan(
        plan([
          { id: "one", after: ["two"] },
          { id: "two", after: ["one"] },
        ]),
      ),
    /cycle/u,
  );
  assert.throws(() => validatePlan(plan([{ id: "Bad Id" }])), /kebab/u);
  assert.throws(
    () => validatePlan({ producer: "test", stages: [{ id: "one", argv: [] }] }),
    /command/u,
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
      return { status: "passed" };
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
    return { status: "passed" };
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
      } else {
        free += active.size;
      }
      await pause(15);
      active.delete(stage.id);
      return { status: "passed" };
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
      if (stage.id === "solo") {
        exclusiveSawOthers = active.size > 0;
      } else {
        othersSawExclusive ||= active.has("solo");
      }
      active.add(stage.id);
      await pause(15);
      active.delete(stage.id);
      return { status: "passed" };
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
      if (stage.id === "thrower") {
        throw new Error("boom");
      }
      return { status: stage.id === "bad" ? "failed" : "passed" };
    },
  );
  const failed = results
    .filter((result) => result.value.status === "failed")
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
      return { status: stage.id === "first" ? "failed" : "passed" };
    },
    { failFast: true },
  );
  assert.deepEqual(started, ["first"]);
  const notStarted = results
    .filter((result) => result.value.status === "not-started")
    .map((result) => result.stage.id)
    .sort();
  assert.deepEqual(notStarted, ["second", "third"]);
});

test("without fail-fast every stage still runs after a failure", async () => {
  /** @type {string[]} */
  const started = [];
  await runPlan(plan([{ id: "first" }, { id: "second", after: ["first"] }]), 2, async (stage) => {
    started.push(stage.id);
    return { status: stage.id === "first" ? "failed" : "passed" };
  });
  assert.deepEqual(started, ["first", "second"]);
});

test("concurrency must be a positive integer", async () => {
  await assert.rejects(
    runPlan(plan([{ id: "one" }]), 0, async () => ({ status: "passed" })),
    /positive/u,
  );
});

test("malformed execution results cannot become a passing task", async () => {
  const results = await runPlan(
    plan([{ id: "invalid" }]),
    1,
    async () => /** @type {import("./types.mjs").StageResult} */ ({}),
  );
  assert.equal(results[0]?.value.status, "failed");
  const contradictory = await runPlan(
    plan([{ id: "contradictory" }]),
    1,
    async () =>
      /** @type {import("./types.mjs").StageResult} */ ({ status: "passed", failed: true }),
  );
  assert.equal(contradictory[0]?.value.status, "failed");
});

test("malformed resource and source-selector metadata is refused before execution", () => {
  for (const extra of [
    { exclusive: "yes" },
    { group: 1 },
    { after: "early" },
    { env: [] },
    { requires: ["node", "node"] },
    { appendFiles: { directories: ["../private"], suffix: ".sh" } },
    { ignoredField: true },
  ]) {
    assert.throws(() =>
      validatePlan({ producer: "fixture", stages: [{ id: "invalid", argv: ["true"], ...extra }] }),
    );
  }
});

test("a ready exclusive task drains running work before later tasks may start", async () => {
  /** @type {string[]} */
  const order = [];
  await runPlan(
    plan([{ id: "first" }, { id: "exclusive", exclusive: true }, { id: "later" }]),
    3,
    async (stage) => {
      order.push(`start ${stage.id}`);
      await pause(5);
      order.push(`end ${stage.id}`);
      return { status: "passed" };
    },
  );
  assert.deepEqual(order, [
    "start first",
    "end first",
    "start exclusive",
    "end exclusive",
    "start later",
    "end later",
  ]);
});
