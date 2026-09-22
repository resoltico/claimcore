import { createRequire } from "node:module";

// One explicit locked tooling dependency; no parser installation during a gate.
const require = createRequire(
  new URL("../../web/package.json", import.meta.url),
);
const { parseDocument, visit, isScalar } = require("yaml");

export function parseWorkflow(source) {
  const document = parseDocument(source, {
    version: "1.2",
    uniqueKeys: true,
    strict: true,
  });
  if (document.errors.length || document.warnings.length)
    throw new Error("Invalid workflow YAML.");
  const actions = [];
  visit(document, {
    Alias() {
      throw new Error("Workflow aliases require explicit policy support.");
    },
    Pair(_, pair) {
      if (pair.key?.value === "<<")
        throw new Error("YAML merge keys are not supported.");
      if (pair.key?.value !== "uses") return;
      if (!isScalar(pair.value) || typeof pair.value.value !== "string") {
        throw new Error("Action references must be literal strings.");
      }
      actions.push({
        reference: pair.value.value,
        comment: pair.value.comment ?? "",
      });
    },
  });
  const value = document.toJS({ maxAliasCount: 0 });
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new Error("Expected workflow mapping.");
  return { value, actions };
}
