import assert from "node:assert/strict";
import { checkGraph } from "./workflow-graph.mjs";
import { checkUploads } from "./workflow-uploads.mjs";
import { parseWorkflow } from "./yaml.mjs";

import {
  publisherPaths,
  standalonePaths,
  falseInput,
  names,
  expression,
} from "./workflow-model.mjs";

const deniedEvents = new Set([
  "pull_request_target",
  "workflow_run",
  "issue_comment",
]);

function checkActions(parsed, path, sources, composite) {
  for (const { reference, comment } of parsed.actions) {
    if (reference.startsWith("./")) {
      assert(
        !reference.includes("..") && !reference.includes("\\"),
        "Unsafe local action path.",
      );
      const local = reference.slice(2);
      assert(
        sources.has(local) ||
          sources.has(`${local}/action.yml`) ||
          sources.has(`${local}/action.yaml`),
        "Missing local action/workflow.",
      );
    } else {
      assert(
        /^[A-Za-z0-9_.\/-]+@[0-9a-f]{40}$/u.test(reference),
        "External actions require a full commit pin.",
      );
      assert(
        /\bv?\d+\.\d+/u.test(comment),
        "Action pin needs its reviewed version comment.",
      );
      if (/^actions\/setup-(?:dotnet|node)@/u.test(reference)) {
        assert(
          composite &&
            /^\.github\/actions\/toolchain\/action\.ya?ml$/u.test(path),
          "Toolchain selection belongs to the composite action.",
        );
      }
    }
  }
}

function checkSteps(steps) {
  for (const step of steps ?? []) {
    if (
      typeof step.uses === "string" &&
      step.uses.startsWith("actions/checkout@")
    ) {
      assert(
        falseInput(step.with?.["persist-credentials"]),
        "Checkout must not persist credentials.",
      );
    }
  }
}

function checkPermissions(value, publisher) {
  const permissions = value.permissions;
  assert(
    permissions && typeof permissions === "object",
    "Explicit workflow permissions are required.",
  );
  assert(
    Object.values(permissions).every(
      (level) => level === "read" || level === "none",
    ),
    "Workflow defaults may not grant writes.",
  );
  for (const job of Object.values(value.jobs ?? {})) {
    assert(
      job["continue-on-error"] === undefined ||
        falseInput(job["continue-on-error"]),
      "Required jobs cannot tolerate failure.",
    );
    if (job.permissions) {
      assert(
        typeof job.permissions === "object",
        "Use explicit job permissions.",
      );
      for (const [permission, level] of Object.entries(job.permissions)) {
        assert(
          ["read", "none", "write"].includes(level),
          "Job permissions must be literal.",
        );
        if (level !== "write") continue;
        assert(
          publisher,
          "Verification and health jobs cannot acquire write authority.",
        );
        assert(
          ["contents", "packages", "id-token", "attestations"].includes(
            permission,
          ),
          "Unreviewed publisher write scope.",
        );
        assert(
          job.environment === "release",
          "Privileged publication requires the release environment.",
        );
        assert(
          [
            "github.ref == 'refs/heads/main'",
            "github.event_name == 'workflow_dispatch' && github.repository == 'resoltico/claimcore' && github.ref == 'refs/heads/main'",
          ].includes(expression(job.if)),
          "Publication must be main-only.",
        );
      }
    }
    checkSteps(job.steps);
    checkUploads(job.steps ?? []);
  }
}

export function validateWorkflowSources(sources, { graph = true } = {}) {
  const workflows = new Map();
  let workflowCount = 0;
  let actionCount = 0;
  for (const [path, source] of sources) {
    const workflow = /^\.github\/workflows\/[^/]+\.ya?ml$/u.test(path);
    const composite = /^\.github\/actions\/.+\/action\.ya?ml$/u.test(path);
    if (!workflow && !composite) continue;
    const parsed = parseWorkflow(source);
    checkActions(parsed, path, sources, composite);
    if (composite) {
      actionCount += 1;
      checkSteps(parsed.value.runs?.steps);
      continue;
    }
    workflowCount += 1;
    const name = path.split("/").at(-1);
    const value = parsed.value;
    assert(
      !names(value.on).some((event) => deniedEvents.has(event)),
      "Privileged event requires a separate reviewed trust design.",
    );
    checkPermissions(value, publisherPaths.has(name));
    if (name !== "ci.yml") {
      assert(
        !Object.values(value.jobs ?? {}).some((job) => job.name === "Gate"),
        "Aggregate Gate name must be unique.",
      );
    }
    workflows.set(name, value);
  }
  assert(workflowCount > 0, "No workflow files inspected.");
  if (graph) {
    assert(workflows.has("ci.yml"), "Missing CI orchestrator.");
    checkGraph(workflows.get("ci.yml"), workflows);
    const evidence = workflows.get("verify-evidence.yml");
    const collections = Object.values(evidence.jobs)
      .flatMap((job) => job.steps ?? [])
      .filter((step) => step.with?.pattern?.startsWith("claimcore-stage-"));
    assert.equal(
      collections.length,
      1,
      "Expected one producer-manifest collection.",
    );
    assert.equal(collections[0].with.path, "artifacts/evidence-producers");
    assert(
      falseInput(collections[0].with["merge-multiple"]),
      "Stage artifacts must retain producer directories.",
    );
    const artifactScan = Object.values(evidence.jobs)
      .flatMap((job) => job.steps ?? [])
      .find((step) => step.id === "artifact_secrets");
    assert(
      typeof artifactScan?.run === "string" &&
        !artifactScan.run.includes("artifacts/evidence/${{ github.run_id }}"),
      "Artifact scanning cannot read its own stage directory before that manifest exists.",
    );
    const allRuns = [...workflows]
      .filter(([path]) => !standalonePaths.has(path))
      .flatMap(([, value]) =>
        Object.values(value.jobs ?? {}).flatMap((job) =>
          (job.steps ?? []).map((step) => step.run ?? ""),
        ),
      )
      .join("\n");
    assert(
      !allRuns.includes("Check-DependencyCurrency.ps1"),
      "Upstream freshness must not gate unrelated PRs.",
    );
    assert.equal(
      (allRuns.match(/run_stage dependency-security /gu) ?? []).length,
      1,
      "Dependency security needs exactly one producer.",
    );
  }
  return { workflows: workflowCount, compositeActions: actionCount };
}
