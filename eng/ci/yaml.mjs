import { isScalar, parseDocument, visit } from "yaml";

/**
 * Parse a workflow with strict YAML 1.2, rejecting aliases, merge keys and non-literal action references.
 * @param {string} source
 * @returns {{ value: import("./types.mjs").Json, actions: { reference: string, comment: string }[] }}
 */
export function parseWorkflow(source) {
  const document = parseDocument(source, {
    version: "1.2",
    uniqueKeys: true,
    strict: true,
  });
  if (document.errors.length || document.warnings.length) throw new Error("Invalid workflow YAML.");
  /** @type {{ reference: string, comment: string }[]} */
  const actions = [];
  visit(document, {
    Alias() {
      throw new Error("Workflow aliases require explicit policy support.");
    },
    Pair(_, pair) {
      const key = isScalar(pair.key) ? pair.key.value : undefined;
      if (key === "<<") throw new Error("YAML merge keys are not supported.");
      if (key !== "uses") return;
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
