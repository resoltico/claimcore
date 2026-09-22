import assert from "node:assert/strict";

import {
  standalonePaths,
  names,
  dependencies,
  expression,
  same,
} from "./workflow-model.mjs";

export function checkGraph(orchestrator, workflows) {
  same(
    names(orchestrator.on),
    ["push", "pull_request", "merge_group", "workflow_dispatch"],
    "CI event inventory changed.",
  );
  assert.deepEqual(
    orchestrator.on.push,
    { branches: ["main"], tags: ["v*"] },
    "CI push coverage must remain complete.",
  );
  assert(
    orchestrator.on.pull_request === null &&
      orchestrator.on.merge_group === null,
    "PR and merge-group checks must not be filtered.",
  );
  const group =
    "${{ github.workflow }}-${{ github.event_name }}-${{ github.event.pull_request.number || github.ref }}";
  assert.equal(
    orchestrator.concurrency?.group,
    group,
    "Concurrency must isolate events and PRs.",
  );
  assert.equal(
    expression(orchestrator.concurrency["cancel-in-progress"]),
    "github.event_name == 'pull_request' || github.event_name == 'merge_group'",
    "Only superseded review work is cancelled.",
  );
  const jobs = orchestrator.jobs;
  assert(jobs?.gate && jobs?.evidence, "Gate and evidence jobs are required.");
  assert.equal(jobs.gate.name, "Gate");
  assert.equal(expression(jobs.gate.if), "always()");
  assert.equal(expression(jobs.evidence.if), "always()");
  same(
    dependencies(jobs.gate),
    Object.keys(jobs).filter((id) => id !== "gate"),
    "Gate omits a verification family.",
  );
  same(
    dependencies(jobs.evidence),
    Object.keys(jobs).filter((id) => !["gate", "evidence"].includes(id)),
    "Evidence omits a producer.",
  );
  same(
    Object.keys(jobs.gate),
    ["name", "if", "needs", "runs-on", "timeout-minutes", "steps"],
    "Gate must not alter execution through extra job settings.",
  );
  const gateSteps = jobs.gate.steps;
  assert.equal(gateSteps.length, 1, "Unexpected Gate steps.");
  same(
    Object.keys(gateSteps[0]),
    ["name", "env", "run"],
    "Gate step cannot be skipped, tolerate failure or substitute its shell.",
  );
  assert.deepEqual(gateSteps[0].env, {
    RESULTS: "${{ join(needs.*.result, ' ') }}",
  });
  assert.equal(gateSteps[0].env?.RESULTS, "${{ join(needs.*.result, ' ') }}");
  assert.equal(
    gateSteps[0].run.trim(),
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
  const called = new Set();
  for (const [id, job] of Object.entries(jobs)) {
    if (id === "gate") continue;
    assert(
      /^\.\/\.github\/workflows\/verify-[a-z-]+\.ya?ml$/u.test(job.uses),
      "Verification family needs a local reusable workflow.",
    );
    assert(
      !called.has(job.uses),
      "A reusable verification family is called twice.",
    );
    assert(
      id === "evidence" || job.if === undefined,
      "Mandatory family must not be conditionally omitted.",
    );
    called.add(job.uses);
    assert(!dependencies(job).includes(id), "Workflow dependency cycle.");
  }
  const visiting = new Set();
  const seen = new Set();
  function walk(id) {
    assert(jobs[id], "Unknown job dependency.");
    assert(!visiting.has(id), "Workflow dependency cycle.");
    if (seen.has(id)) return;
    visiting.add(id);
    dependencies(jobs[id]).forEach(walk);
    visiting.delete(id);
    seen.add(id);
  }
  Object.keys(jobs).forEach(walk);
  for (const [path, value] of workflows) {
    if (path === "ci.yml" || standalonePaths.has(path)) continue;
    assert(
      Object.values(value.jobs).every((job) => job.if === undefined),
      "Reusable verification jobs must not be conditionally omitted.",
    );
    same(
      names(value.on),
      ["workflow_call"],
      "Reusable verifier must not self-trigger.",
    );
    assert(
      called.has(`./.github/workflows/${path}`),
      "Reusable verifier is disconnected from Gate.",
    );
  }
}
