// Scheduling for a plan of independent verification stages.
//
// A plan lists stages, each with the command that proves it, the stages it must follow, and an
// optional resource group. Stages in one group never overlap (they share a port, a container
// namespace or a build output); an exclusive stage (one that writes into the source tree other
// stages read) runs alone. Everything else runs as soon as its predecessors have finished, up to
// the requested concurrency. Every stage runs to completion whether or not another failed: a
// verification run reports all findings, not the first.

import { validateStageMetadata } from "./stage-metadata.mjs";

const stageId = /^[a-z0-9]+(-[a-z0-9]+)*$/u;

/** @param {import("./types.mjs").Stage[]} stages @returns {Set<string>} */
function checkStages(stages) {
  const ids = new Set();
  for (const stage of stages) {
    validateStageMetadata(stage);
    if (typeof stage.id !== "string" || !stageId.test(stage.id)) {
      throw new Error("Stage ids are lowercase kebab-case.");
    }
    if (ids.has(stage.id)) {
      throw new Error(`Stage '${stage.id}' is listed twice.`);
    }
    ids.add(stage.id);
    const command = stage.argv;
    if (
      !Array.isArray(command) ||
      command.length === 0 ||
      command.some((part) => typeof part !== "string")
    ) {
      throw new Error(`Stage '${stage.id}' needs a command.`);
    }
  }
  return ids;
}

/** @param {import("./types.mjs").Stage[]} stages @param {Set<string>} ids */
function checkPredecessors(stages, ids) {
  for (const stage of stages) {
    for (const predecessor of stage.after ?? []) {
      if (!ids.has(predecessor)) {
        throw new Error(`Stage '${stage.id}' follows unknown stage '${predecessor}'.`);
      }
      if (predecessor === stage.id) {
        throw new Error(`Stage '${stage.id}' follows itself.`);
      }
    }
  }
}

/** Reject cycles: repeatedly remove stages whose predecessors are gone. @param {import("./types.mjs").Stage[]} stages */
function checkAcyclic(stages) {
  const remaining = new Map(stages.map((stage) => [stage.id, new Set(stage.after ?? [])]));
  while (remaining.size > 0) {
    const free = [...remaining].filter(([, needs]) => needs.size === 0).map(([id]) => id);
    if (free.length === 0) {
      throw new Error("Stage predecessors form a cycle.");
    }
    for (const id of free) {
      remaining.delete(id);
    }
    for (const needs of remaining.values()) {
      for (const id of free) {
        needs.delete(id);
      }
    }
  }
}

/**
 * @param {unknown} candidate
 * @returns {import("./types.mjs").Plan}
 */
export function validatePlan(candidate) {
  const plan = /** @type {import("./types.mjs").Plan} */ (candidate);
  if (
    !plan ||
    typeof plan.producer !== "string" ||
    !Array.isArray(plan.stages) ||
    plan.stages.length === 0
  ) {
    throw new Error("A stage plan needs a producer and a stage list.");
  }
  const ids = checkStages(plan.stages);
  checkPredecessors(plan.stages, ids);
  checkAcyclic(plan.stages);
  return plan;
}

/** @typedef {{ stage: import("./types.mjs").Stage, value: import("./types.mjs").StageResult }} Outcome */

/** The bookkeeping that decides which pending stage may start next. */
class Schedule {
  /** @type {import("./types.mjs").Stage[]} */
  pending;
  /** @type {Set<string>} */
  finished = new Set();
  /** @type {Set<string>} */
  busyGroups = new Set();
  running = 0;
  exclusiveRunning = false;
  failed = false;

  /** @param {import("./types.mjs").Stage[]} stages @param {boolean} failFast */
  constructor(stages, failFast) {
    this.pending = [...stages];
    this.failFast = failFast;
  }

  /** @returns {import("./types.mjs").Stage | undefined} */
  next() {
    if (this.exclusiveRunning || (this.failFast && this.failed)) {
      return undefined;
    }
    for (const stage of this.pending) {
      if (!(stage.after ?? []).every((id) => this.finished.has(id))) {
        continue;
      }
      if (stage.exclusive === true) {
        return this.running === 0 ? stage : undefined;
      }
      if (stage.group === undefined || !this.busyGroups.has(stage.group)) {
        return stage;
      }
    }
    return undefined;
  }

  /** @param {import("./types.mjs").Stage} stage */
  start(stage) {
    if (stage.exclusive === true) {
      this.exclusiveRunning = true;
    }
    this.pending.splice(this.pending.indexOf(stage), 1);
    if (stage.group !== undefined) {
      this.busyGroups.add(stage.group);
    }
    this.running += 1;
  }

  /** @param {Outcome} outcome */
  finish({ stage, value }) {
    this.running -= 1;
    this.finished.add(stage.id);
    if (stage.exclusive === true) {
      this.exclusiveRunning = false;
    }
    if (stage.group !== undefined) {
      this.busyGroups.delete(stage.group);
    }
    if (value.status === "failed") {
      this.failed = true;
    }
  }
}

/** @param {number} parallel */
function checkConcurrency(parallel) {
  if (!Number.isInteger(parallel) || parallel < 1) {
    throw new Error("Concurrency must be a positive integer.");
  }
}

/** @param {import("./types.mjs").StageResult} value @returns {import("./types.mjs").StageResult} */
function requireResult(value) {
  if (!value || !["passed", "failed", "skipped"].includes(value.status)) {
    throw new Error("A task returned an invalid execution result.");
  }
  const fields = new Set(["status"]);
  if (value.status !== "passed") {
    fields.add("note");
  }
  if (value.status === "failed") {
    fields.add("error");
  }
  if (Object.keys(value).some((key) => !fields.has(key))) {
    throw new Error("A task returned contradictory execution metadata.");
  }
  return value;
}

/**
 * Start every stage that may start, up to the concurrency bound.
 * @param {Schedule} schedule
 * @param {Map<string, Promise<Outcome>>} running
 * @param {number} parallel
 * @param {(stage: import("./types.mjs").Stage) => Promise<import("./types.mjs").StageResult>} runStage
 */
function launch(schedule, running, parallel, runStage) {
  for (let next = schedule.next(); running.size < parallel && next; next = schedule.next()) {
    const stage = next;
    schedule.start(stage);
    running.set(
      stage.id,
      Promise.resolve()
        .then(() => runStage(stage))
        .then(requireResult)
        .then(
          (value) => ({ stage, value }),
          (error) => ({ stage, value: { status: "failed", error } }),
        ),
    );
  }
}

/**
 * Run every stage of `plan`, `parallel` at a time, through `runStage(stage)`, which returns a
 * promise. Resolves with the results in completion order. Never rejects for a failing stage.
 * With `failFast`, no stage starts after one has failed: stages already running finish, and the
 * rest are reported as not started (`status: not-started` in the result).
 * @param {import("./types.mjs").Plan} plan
 * @param {number} parallel
 * @param {(stage: import("./types.mjs").Stage) => Promise<import("./types.mjs").StageResult>} runStage
 * @param {{ failFast?: boolean }} [options]
 * @returns {Promise<Outcome[]>}
 */
export async function runPlan(plan, parallel, runStage, { failFast = false } = {}) {
  validatePlan(plan);
  checkConcurrency(parallel);
  const schedule = new Schedule(plan.stages, failFast);
  /** @type {Map<string, Promise<Outcome>>} */
  const running = new Map();
  /** @type {Outcome[]} */
  const results = [];
  while (schedule.pending.length > 0 || running.size > 0) {
    launch(schedule, running, parallel, runStage);
    if (running.size === 0) {
      if (failFast && schedule.failed) {
        break;
      }
      throw new Error("No stage can start; the plan is stuck.");
    }
    const done = await Promise.race(running.values());
    running.delete(done.stage.id);
    schedule.finish(done);
    results.push(done);
  }
  return failFast && schedule.failed
    ? [
        ...results,
        ...schedule.pending.map((stage) => ({
          stage,
          value: { status: /** @type {const} */ ("not-started") },
        })),
      ]
    : results;
}
