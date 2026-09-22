import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { workflowSources } from "./workflow-sources.mjs";
import { validateWorkflowSources } from "./workflow-policy.mjs";

const root =
  process.argv[2] ?? fileURLToPath(new URL("../..", import.meta.url));
try {
  if (process.argv.length > 3)
    throw new Error("Expected at most one repository root.");
  const result = validateWorkflowSources(workflowSources(resolve(root)));
  console.log(
    `Workflow policy passed: ${result.workflows} workflows, ${result.compositeActions} composite actions.`,
  );
} catch (error) {
  // Checker messages contain policy expectations, never parsed user payloads.
  console.error(`Workflow policy refused: ${error.message}`);
  process.exitCode = 1;
}
