import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { workflowSources } from "./workflow-sources.mjs";
import { validateWorkflowSources } from "./workflow-policy.mjs";
import { checkReferences } from "./workflow-references.mjs";

const root = process.argv[2] ?? fileURLToPath(new URL("../..", import.meta.url));
try {
  if (process.argv.length > 3) {
    throw new Error("Expected at most one repository root.");
  }
  const result = validateWorkflowSources(workflowSources(resolve(root)));
  const missing = checkReferences(resolve(root));
  if (missing.length > 0) {
    throw new Error(missing.join("\n"));
  }
  console.log(
    `Workflow policy passed: ${result.workflows} workflows, ${result.compositeActions} composite actions.`,
  );
} catch (error) {
  // Checker messages contain policy expectations, never parsed user payloads.
  console.error(`Workflow policy refused: ${error instanceof Error ? error.message : "unknown"}`);
  process.exitCode = 1;
}
