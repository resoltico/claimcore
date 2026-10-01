import assert from "node:assert/strict";
import { checkGraph } from "./workflow-graph.mjs";
import { checkUploads } from "./workflow-uploads.mjs";
import { parseWorkflow } from "./yaml.mjs";

import { publisherPaths, falseInput, names, expression } from "./workflow-model.mjs";

/** @typedef {import("./types.mjs").Json} Json */

const deniedEvents = new Set(["pull_request_target", "workflow_run", "issue_comment"]);
const mainOnly = [
  "github.ref == 'refs/heads/main'",
  "github.event_name == 'workflow_dispatch' && github.repository == 'resoltico/claimcore' && github.ref == 'refs/heads/main'",
];

/**
 * @param {string} reference
 * @param {Set<string>} sources
 */
function checkLocalAction(reference, sources) {
  assert(!reference.includes("..") && !reference.includes("\\"), "Unsafe local action path.");
  const local = reference.slice(2);
  assert(
    sources.has(local) || sources.has(`${local}/action.yml`) || sources.has(`${local}/action.yaml`),
    "Missing local action/workflow.",
  );
}

/**
 * @param {string} reference
 * @param {string} comment
 * @param {{ path: string, composite: boolean }} where
 */
function checkExternalAction(reference, comment, { path, composite }) {
  assert(
    /^[A-Za-z0-9_./-]+@[0-9a-f]{40}$/u.test(reference),
    "External actions require a full commit pin.",
  );
  assert(/\bv?\d+\.\d+/u.test(comment), "Action pin needs its reviewed version comment.");
  if (/^actions\/setup-(?:dotnet|node)@/u.test(reference)) {
    assert(
      composite && /^\.github\/actions\/toolchain\/action\.ya?ml$/u.test(path),
      "Toolchain selection belongs to the composite action.",
    );
  }
}

/**
 * @param {ReturnType<typeof parseWorkflow>} parsed
 * @param {string} path
 * @param {Set<string>} sources
 * @param {boolean} composite
 */
function checkActions(parsed, path, sources, composite) {
  for (const { reference, comment } of parsed.actions) {
    if (reference.startsWith("./")) {
      checkLocalAction(reference, sources);
    } else {
      checkExternalAction(reference, comment, { path, composite });
    }
  }
}

/** @param {Json[] | undefined} steps */
function checkSteps(steps) {
  for (const step of steps ?? []) {
    if (typeof step["uses"] === "string" && step["uses"].startsWith("actions/checkout@")) {
      assert(
        falseInput(step["with"]?.["persist-credentials"]),
        "Checkout must not persist credentials.",
      );
    }
  }
}

/**
 * @param {string} permission
 * @param {string} level
 * @param {Json} job
 * @param {boolean} publisher
 */
function checkWrite(permission, level, job, publisher) {
  assert(["read", "none", "write"].includes(level), "Job permissions must be literal.");
  if (level !== "write") {
    return;
  }
  assert(publisher, "Verification and health jobs cannot acquire write authority.");
  assert(
    ["contents", "packages", "id-token", "attestations"].includes(permission),
    "Unreviewed publisher write scope.",
  );
  assert(
    job["environment"] === "release",
    "Privileged publication requires the release environment.",
  );
  assert(mainOnly.includes(expression(job["if"])), "Publication must be main-only.");
}

/**
 * Runner images are pinned to an exact label so a moving `-latest` alias cannot change a job.
 * @param {Json} job
 */
function checkRunners(job) {
  const include = job["strategy"]?.["matrix"]?.["include"];
  const labels = [
    job["runs-on"],
    ...(Array.isArray(include) ? include.map((/** @type {Json} */ entry) => entry["os"]) : []),
  ];
  for (const label of labels) {
    assert(
      typeof label !== "string" || !/-latest$/u.test(label),
      "Runner images must be pinned, never -latest.",
    );
  }
}

/**
 * @param {Json} job
 * @param {boolean} publisher
 */
function checkJob(job, publisher) {
  checkRunners(job);
  assert(
    job["continue-on-error"] === undefined || falseInput(job["continue-on-error"]),
    "Required jobs cannot tolerate failure.",
  );
  if (job["permissions"]) {
    assert(typeof job["permissions"] === "object", "Use explicit job permissions.");
    for (const [permission, level] of Object.entries(job["permissions"])) {
      checkWrite(permission, String(level), job, publisher);
    }
  }
  checkSteps(job["steps"]);
  checkUploads(job["steps"] ?? []);
}

/**
 * @param {Json} value
 * @param {boolean} publisher
 */
function checkPermissions(value, publisher) {
  const { permissions } = value;
  assert(
    permissions && typeof permissions === "object",
    "Explicit workflow permissions are required.",
  );
  assert(
    Object.values(permissions).every((level) => level === "read" || level === "none"),
    "Workflow defaults may not grant writes.",
  );
  for (const job of Object.values(value["jobs"] ?? {})) {
    checkJob(job, publisher);
  }
}

/**
 * Check every workflow and composite action against the reviewed structure.
 * @param {Map<string, string>} sources
 * @param {{ graph?: boolean }} [options]
 */
export function validateWorkflowSources(sources, { graph = true } = {}) {
  /** @type {Map<string, Json>} */
  const workflows = new Map();
  let workflowCount = 0;
  let actionCount = 0;
  const known = new Set(sources.keys());
  for (const [path, source] of sources) {
    const workflow = /^\.github\/workflows\/[^/]+\.ya?ml$/u.test(path);
    const composite = /^\.github\/actions\/.+\/action\.ya?ml$/u.test(path);
    if (!workflow && !composite) {
      continue;
    }
    const parsed = parseWorkflow(source);
    checkActions(parsed, path, known, composite);
    if (composite) {
      actionCount += 1;
      checkSteps(parsed.value["runs"]?.steps);
      continue;
    }
    workflowCount += 1;
    const name = path.split("/").at(-1) ?? path;
    const { value } = parsed;
    assert(
      !names(value["on"]).some((event) => deniedEvents.has(event)),
      "Privileged event requires a separate reviewed trust design.",
    );
    checkPermissions(value, publisherPaths.has(name));
    if (name !== "ci.yml") {
      assert(
        !Object.values(value["jobs"] ?? {}).some((job) => job.name === "Gate"),
        "Aggregate Gate name must be unique.",
      );
    }
    workflows.set(name, value);
  }
  assert(workflowCount > 0, "No workflow files inspected.");
  if (graph) {
    const orchestrator = workflows.get("ci.yml");
    assert(orchestrator, "Missing CI orchestrator.");
    checkGraph(orchestrator, workflows);
  }
  return { workflows: workflowCount, compositeActions: actionCount };
}
