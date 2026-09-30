// Scheduling for a plan of independent verification stages.
//
// A plan lists stages, each with the command that proves it, the stages it must follow, and an
// optional resource group. Stages in one group never overlap (they share a port, a container
// namespace or a build output); an exclusive stage (one that writes into the source tree other
// stages read) runs alone. Everything else runs as soon as its predecessors have finished, up to
// the requested concurrency. Every stage runs to completion whether or not another failed: a
// verification run reports all findings, not the first.

export function validatePlan(plan) {
  if (!plan || typeof plan.producer !== "string" || !Array.isArray(plan.stages))
    throw new Error("A stage plan needs a producer and a stage list.");
  const ids = new Set();
  for (const stage of plan.stages) {
    if (
      typeof stage.id !== "string" ||
      !/^[a-z0-9]+(-[a-z0-9]+)*$/.test(stage.id)
    )
      throw new Error("Stage ids are lowercase kebab-case.");
    if (ids.has(stage.id))
      throw new Error(`Stage '${stage.id}' is listed twice.`);
    ids.add(stage.id);
    if (
      !Array.isArray(stage.argv) ||
      stage.argv.length === 0 ||
      stage.argv.some((part) => typeof part !== "string")
    )
      throw new Error(`Stage '${stage.id}' needs a command.`);
  }
  for (const stage of plan.stages)
    for (const predecessor of stage.after ?? []) {
      if (!ids.has(predecessor))
        throw new Error(
          `Stage '${stage.id}' follows unknown stage '${predecessor}'.`,
        );
      if (predecessor === stage.id)
        throw new Error(`Stage '${stage.id}' follows itself.`);
    }
  // Reject cycles: repeatedly remove stages whose predecessors are gone.
  const remaining = new Map(
    plan.stages.map((stage) => [stage.id, new Set(stage.after ?? [])]),
  );
  while (remaining.size > 0) {
    const free = [...remaining]
      .filter(([, needs]) => needs.size === 0)
      .map(([id]) => id);
    if (free.length === 0) throw new Error("Stage predecessors form a cycle.");
    for (const id of free) remaining.delete(id);
    for (const needs of remaining.values())
      for (const id of free) needs.delete(id);
  }
  return plan;
}

/**
 * Run every stage of `plan`, `parallel` at a time, through `runStage(stage)`, which returns a
 * promise. Resolves with the results in completion order. Never rejects for a failing stage.
 */
export async function runPlan(plan, parallel, runStage) {
  validatePlan(plan);
  if (!Number.isInteger(parallel) || parallel < 1)
    throw new Error("Concurrency must be a positive integer.");
  const pending = [...plan.stages];
  const finished = new Set();
  const running = new Map();
  const busyGroups = new Set();
  const results = [];

  let exclusiveRunning = false;
  const launchable = () => {
    if (exclusiveRunning) return [];
    return pending.filter(
      (stage) =>
        (stage.after ?? []).every((id) => finished.has(id)) &&
        (stage.group === undefined || !busyGroups.has(stage.group)) &&
        // An exclusive stage waits for the running stages to drain; nothing may start behind it.
        (stage.exclusive !== true || running.size === 0),
    );
  };

  while (pending.length > 0 || running.size > 0) {
    while (running.size < parallel) {
      const next = launchable()[0];
      if (next === undefined) break;
      if (next.exclusive === true) exclusiveRunning = true;
      pending.splice(pending.indexOf(next), 1);
      if (next.group !== undefined) busyGroups.add(next.group);
      running.set(
        next.id,
        Promise.resolve()
          .then(() => runStage(next))
          .then(
            (value) => ({ stage: next, value }),
            (error) => ({ stage: next, value: { failed: true, error } }),
          ),
      );
    }
    if (running.size === 0)
      throw new Error("No stage can start; the plan is stuck.");
    const done = await Promise.race(running.values());
    running.delete(done.stage.id);
    finished.add(done.stage.id);
    if (done.stage.exclusive === true) exclusiveRunning = false;
    if (done.stage.group !== undefined) busyGroups.delete(done.stage.group);
    results.push(done);
  }
  return results;
}
