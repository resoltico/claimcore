// Check literal executable source references; generated outputs are a different responsibility.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { parseWorkflow } from "./yaml.mjs";
import { repositoryFiles } from "./repository.mjs";
import { workflowSources } from "./workflow-sources.mjs";
import { shellCommands } from "./shell-words.mjs";

/** @param {string} script @returns {string[]} */
function scriptReferences(script) {
  const found = [];
  for (const words of shellCommands(script)) {
    const index = words.findIndex((word) => ["node", "bash", "sh"].includes(word));
    const prefixes = words.slice(0, index);
    const direct = prefixes.every(
      (word) =>
        ["exec", "if", "!", "then", "do", "env"].includes(word) ||
        /^[A-Za-z_][A-Za-z0-9_]*=/u.test(word),
    );
    const path = words[index + 1] ?? "";
    if (index >= 0 && direct && /^eng\/[A-Za-z0-9._/-]+\.(?:mjs|sh)$/u.test(path)) {
      found.push(path);
    }
  }
  return found;
}

/** @param {string[]} argv @returns {string[]} */
function argvReferences(argv) {
  const [command = "", argument, script] = argv;
  const found = [];
  if (
    ["node", "bash", "sh"].includes(command) &&
    typeof argument === "string" &&
    argument.startsWith("eng/")
  ) {
    found.push(argument);
  }
  if (["bash", "sh"].includes(command) && argument === "-c" && typeof script === "string") {
    found.push(...scriptReferences(script));
  }
  return found;
}

/** @param {import("./types.mjs").Json} value @returns {string[]} */
function references(value) {
  const found = [];
  for (const [key, child] of Object.entries(value)) {
    if (key === "run" && typeof child === "string") {
      found.push(...scriptReferences(child));
    } else if (key === "uses" && typeof child === "string" && child.startsWith("./")) {
      found.push(child.slice(2));
    } else if (key === "argv" && Array.isArray(child)) {
      found.push(...argvReferences(child));
    } else if (child !== null && typeof child === "object") {
      found.push(...references(child));
    }
  }
  return found;
}

/** @param {string} source @param {import("./types.mjs").Json} value @returns {string[]} */
function securityProblems(source, value) {
  const errors = [];
  if (source === "eng/ci/stage-plans/quality.json") {
    const security = value.stages.find(
      (/** @type {import("./types.mjs").Stage} */ stage) => stage.id === "workflow-security",
    );
    const args = security?.argv ?? [];
    const persona = args.indexOf("--persona");
    if (
      !args.includes("zizmor") ||
      args.includes("--no-exit-codes") ||
      persona < 0 ||
      args[persona + 1] !== "pedantic"
    ) {
      errors.push("The workflow-security stage must fail on pedantic security findings.");
    }
  }
  return errors;
}

/** @param {Map<string, string>} sources @param {string[]} files @returns {string[]} */
export function validateReferences(sources, files) {
  const known = new Set(files);
  const errors = [];
  for (const [source, text] of sources) {
    const value = source.endsWith(".json") ? JSON.parse(text) : parseWorkflow(text).value;
    for (const path of new Set(references(value))) {
      if (
        !known.has(path) &&
        !known.has(`${path}/action.yml`) &&
        !known.has(`${path}/action.yaml`)
      ) {
        errors.push(`${source} executes missing source ${path}.`);
      }
    }
    errors.push(...securityProblems(source, value));
  }
  return errors;
}

/** @param {string} root @returns {string[]} */
export function checkReferences(root) {
  const sources = workflowSources(root);
  sources.set("eng/ci/local-plan.json", readFileSync(join(root, "eng/ci/local-plan.json"), "utf8"));
  return validateReferences(sources, repositoryFiles(root));
}
