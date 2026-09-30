import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { isObject, Report } from "./model.mjs";

const identifier = /^LX-\d{4}$/;
const tools = new Set([
  "fsc",
  "fsharplint",
  "dotnet-analyzer",
  "fsharp-analyzers",
  "oxlint",
  "tsc",
  "prettier",
  "stylelint",
  "coverage",
  "knip",
  "ruff",
  "mypy",
  "psscriptanalyzer",
  "shellcheck",
  "yamllint",
  "actionlint",
  "msbuild",
]);
const nonSuppressible =
  /(^|[-_/.:])(max[-_]?lines|file[-_]?size|function[-_]?size|complexity|focused|skip(?:ped)?[-_]?test|test[-_]?filter|contract[-_]?drift|security[-_]?boundary)([-_/.:]|$)/i;
const generatedPath =
  /^(?:artifacts\/obj\/|src\/ClaimCore\.Web\/wwwroot\/|web\/(?:artifacts|coverage|dist|node_modules|playwright-report|test-results)\/)$/;

/**
 * @param {Record<string, unknown>} entry
 * @param {string} name
 */
function text(entry, name) {
  const value = entry[name];
  return typeof value === "string" ? value.trim() : "";
}

/**
 * Owner, reason and a review or expiry date that has not passed.
 * @param {Record<string, unknown>} entry
 * @param {string} label
 * @param {Report} report
 * @param {Date} today
 * @returns {boolean} Whether the entry is governed.
 */
function checkGovernance(entry, label, report, today) {
  const before = report.errors.length;
  if (text(entry, "owner").length < 3) report.add(`${label} needs a named owner.`);
  if (text(entry, "reason").length < 20) report.add(`${label} needs a substantive reason.`);
  const dates = ["reviewOn", "expiresOn"].filter((name) => text(entry, name) !== "");
  if (dates.length === 0) report.add(`${label} requires reviewOn or expiresOn.`);
  for (const name of dates) {
    const value = text(entry, name);
    if (!/^\d{4}-\d{2}-\d{2}$/.test(value) || Number.isNaN(Date.parse(value))) {
      report.add(`${label} ${name} must be an ISO yyyy-MM-dd date.`);
    } else if (new Date(`${value}T23:59:59Z`) < today) {
      report.add(`${label} passed its ${name} date ${value}.`);
    }
  }
  return report.errors.length === before;
}

/**
 * What is wrong with an entry's own fields, independent of governance.
 * @param {Record<string, unknown>} entry
 * @param {string} root
 * @param {Set<string>} ids Ids seen so far; the entry's own id is added.
 * @param {string[]} rules The entry's rules that are strings.
 * @returns {string[]}
 */
function fieldProblems(entry, root, ids, rules) {
  const id = text(entry, "id");
  const file = text(entry, "file");
  const kind = text(entry, "kind");
  const count = entry["count"];
  const listed = Array.isArray(entry["rules"]) ? entry["rules"].length : -1;
  const duplicate = ids.has(id);
  ids.add(id);
  /** @type {Array<[boolean, string]>} */
  const checks = [
    [identifier.test(id), "needs an id of the form LX-0000."],
    [!duplicate, "duplicates another id."],
    [tools.has(text(entry, "tool")), `names an unknown tool '${text(entry, "tool")}'.`],
    [kind === "inline" || kind === "config", "kind must be inline or config."],
    [Number.isInteger(count) && Number(count) >= 1, "needs a positive integer count."],
    [rules.length > 0 && rules.length === listed, "needs exact rule names."],
    [
      rules.every(
        (rule) =>
          rule !== "*" && !nonSuppressible.test(rule) && (kind === "config" || !/[*?]/.test(rule)),
      ),
      "names a blanket, wildcard or non-suppressible rule.",
    ],
    [
      file !== "" && !/[*?]/.test(file) && !file.endsWith("/") && !file.startsWith("/"),
      "must name one exact repository-relative file.",
    ],
    [existsSync(join(root, file)), `points to a file that does not exist: ${file}.`],
  ];
  return checks.filter(([ok]) => !ok).map(([, message]) => message);
}

/**
 * @param {Record<string, unknown>} entry
 * @param {string} root
 * @param {Report} report
 * @param {Set<string>} ids
 * @returns {import("./model.mjs").LintException | null}
 */
function exceptionEntry(entry, root, report, ids) {
  const id = text(entry, "id");
  const label = `Exception ${id || "(no id)"}`;
  const rules = Array.isArray(entry["rules"])
    ? entry["rules"].filter((rule) => typeof rule === "string")
    : [];
  const problems = fieldProblems(entry, root, ids, rules);
  for (const message of problems) report.add(`${label} ${message}`);
  const governed = checkGovernance(entry, label, report, new Date());
  if (problems.length > 0 || !governed) return null;
  return /** @type {import("./model.mjs").LintException} */ ({
    id,
    tool: text(entry, "tool"),
    rules,
    file: text(entry, "file"),
    kind: text(entry, "kind"),
    count: Number(entry["count"]),
    reason: text(entry, "reason"),
    owner: text(entry, "owner"),
  });
}

/**
 * @param {Record<string, unknown>} entry
 * @param {Report} report
 * @param {Set<string>} seen
 * @returns {import("./model.mjs").GeneratedExclusion | null}
 */
function generatedEntry(entry, report, seen) {
  const path = text(entry, "path");
  const label = `Generated exclusion ${path || "(no path)"}`;
  const before = report.errors.length;
  if (!generatedPath.test(path))
    report.add(`${label} is not an exact recognized generated-output path.`);
  if (text(entry, "generator").length < 8)
    report.add(`${label} must name its generator specifically.`);
  if (seen.has(path)) report.add(`${label} is listed twice.`);
  seen.add(path);
  checkGovernance(entry, label, report, new Date());
  if (report.errors.length !== before) return null;
  return {
    path,
    generator: text(entry, "generator"),
    reason: text(entry, "reason"),
    owner: text(entry, "owner"),
  };
}

/**
 * Load and validate the registry; problems go to `report`.
 * @param {string} root Repository root.
 * @param {string} registryPath Absolute path to the registry file.
 * @param {Report} report
 * @returns {import("./model.mjs").Registry}
 */
export function loadRegistry(root, registryPath, report) {
  /** @type {unknown} */
  let parsed;
  try {
    parsed = JSON.parse(readFileSync(registryPath, "utf8"));
  } catch (error) {
    report.add(
      `The lint exception registry cannot be read: ${error instanceof Error ? error.message : String(error)}`,
    );
    return { generated: [], exceptions: [] };
  }
  if (!isObject(parsed) || parsed["version"] !== 2) {
    report.add("The lint exception registry must be an object with version 2.");
    return { generated: [], exceptions: [] };
  }
  const generatedSeen = new Set();
  const ids = new Set();
  const generated = (Array.isArray(parsed["generated"]) ? parsed["generated"] : [])
    .filter(isObject)
    .map((entry) => generatedEntry(entry, report, generatedSeen));
  const exceptions = (Array.isArray(parsed["exceptions"]) ? parsed["exceptions"] : [])
    .filter(isObject)
    .map((entry) => exceptionEntry(entry, root, report, ids))
    .filter((entry) => entry !== null);
  return { generated: generated.filter((entry) => entry !== null), exceptions };
}
