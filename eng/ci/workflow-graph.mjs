import assert from "node:assert/strict";

import { standalonePaths, names, dependencies, expression, same } from "./workflow-model.mjs";

/** @typedef {import("./types.mjs").Json} Json */

/** @param {Json} orchestrator */
function checkTriggers(orchestrator) {
  same(
    names(orchestrator["on"]),
    ["push", "pull_request", "merge_group", "workflow_dispatch"],
    "CI event inventory changed.",
  );
  assert.deepEqual(
    orchestrator["on"].push,
    { branches: ["main"], tags: ["v*"] },
    "CI push coverage must remain complete.",
  );
  assert(
    orchestrator["on"].pull_request === null && orchestrator["on"].merge_group === null,
    "PR and merge-group checks must not be filtered.",
  );
  const group =
    "${{ github.workflow }}-${{ github.event_name }}-${{ github.event.pull_request.number || github.ref }}";
  assert.equal(
    orchestrator["concurrency"]?.group,
    group,
    "Concurrency must isolate events and PRs.",
  );
  assert.equal(
    expression(orchestrator["concurrency"]["cancel-in-progress"]),
    "github.event_name == 'pull_request' || github.event_name == 'merge_group'",
    "Only superseded review work is cancelled.",
  );
}

/** @param {Json} gate */
function checkGateStep(gate) {
  const { steps } = gate;
  assert.equal(steps.length, 1, "Unexpected Gate steps.");
  same(
    Object.keys(steps[0]),
    ["name", "env", "run"],
    "Gate step cannot be skipped, tolerate failure or substitute its shell.",
  );
  assert.deepEqual(steps[0].env, { RESULTS: "${{ join(needs.*.result, ' ') }}" });
  assert.equal(
    steps[0].run.trim(),
    [
      "set -euo pipefail",
      'echo "Verification family results: $RESULTS"',
      'test -n "$RESULTS"',
      "for result in $RESULTS; do",
      '  test "$result" = "success"',
      "done",
    ].join("\n"),
    "Gate must reject every non-success outcome.",
  );
}

/** @param {Json} jobs */
function checkGateJob(jobs) {
  assert(jobs["gate"], "A Gate job is required.");
  assert.equal(jobs["gate"].name, "Gate");
  assert.equal(expression(jobs["gate"].if), "always()");
  same(
    dependencies(jobs["gate"]),
    Object.keys(jobs).filter((id) => id !== "gate"),
    "Gate omits a verification family.",
  );
  same(
    Object.keys(jobs["gate"]),
    ["name", "if", "needs", "runs-on", "timeout-minutes", "steps"],
    "Gate must not alter execution through extra job settings.",
  );
  checkGateStep(jobs["gate"]);
}

/**
 * Every family is one unconditional local reusable workflow, called once.
 * @param {Json} jobs
 * @returns {Set<string>} The called workflows.
 */
function checkFamilies(jobs) {
  const called = new Set();
  for (const [id, job] of Object.entries(jobs)) {
    if (id === "gate") {
      continue;
    }
    assert(
      /^\.\/\.github\/workflows\/verify-[a-z-]+\.ya?ml$/u.test(job.uses),
      "Verification family needs a local reusable workflow.",
    );
    assert(!called.has(job.uses), "A reusable verification family is called twice.");
    assert(job.if === undefined, "Mandatory family must not be conditionally omitted.");
    called.add(job.uses);
    assert(!dependencies(job).includes(id), "Workflow dependency cycle.");
  }
  return called;
}

/** @param {Json} jobs */
function checkAcyclic(jobs) {
  const visiting = new Set();
  const seen = new Set();
  /** @param {string} id */
  function walk(id) {
    assert(jobs[id], "Unknown job dependency.");
    assert(!visiting.has(id), "Workflow dependency cycle.");
    if (seen.has(id)) {
      return;
    }
    visiting.add(id);
    dependencies(jobs[id]).forEach(walk);
    visiting.delete(id);
    seen.add(id);
  }
  Object.keys(jobs).forEach(walk);
}

/**
 * @param {Map<string, Json>} workflows
 * @param {Set<string>} called
 */
function checkReusable(workflows, called) {
  for (const [path, value] of workflows) {
    if (path === "ci.yml" || standalonePaths.has(path)) {
      continue;
    }
    assert(
      Object.values(value["jobs"]).every((job) => job.if === undefined),
      "Reusable verification jobs must not be conditionally omitted.",
    );
    same(names(value["on"]), ["workflow_call"], "Reusable verifier must not self-trigger.");
    assert(
      called.has(`./.github/workflows/${path}`),
      "Reusable verifier is disconnected from Gate.",
    );
  }
}

/**
 * The orchestrating workflow's event, gate, family and dependency structure.
 * @param {Json} orchestrator
 * @param {Map<string, Json>} workflows
 */
export function checkGraph(orchestrator, workflows) {
  checkTriggers(orchestrator);
  const { jobs } = orchestrator;
  checkGateJob(jobs);
  const called = checkFamilies(jobs);
  checkAcyclic(jobs);
  checkReusable(workflows, called);
}
