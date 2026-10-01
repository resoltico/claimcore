// Runtime validation of suite inputs; parsed JSON is not trusted by its static annotation.
const fields = new Set([
  "id",
  "kind",
  "assembly",
  "project",
  "configuration",
  "coverage",
  "platforms",
  "timeout",
  "build",
  "msbuild",
  "env",
  "group",
  "partitions",
]);
const kinds = new Set(["dotnet", "vitest", "playwright"]);
const platforms = new Set(["linux", "macos", "windows"]);
const identifier = /^[a-z][a-z0-9-]*$/u;
const projectPath = /^(?:src|tests|eng)\/[A-Za-z0-9./_-]+\.fsproj$/u;

/** @param {unknown} value @returns {value is string[]} */
const uniqueStrings = (value) =>
  Array.isArray(value) &&
  value.length > 0 &&
  value.every((item) => typeof item === "string" && item.length > 0) &&
  new Set(value).size === value.length;
/** @param {string} value */
const safeProject = (value) => projectPath.test(value) && !value.split("/").includes("..");

/** @param {import("./registry.mjs").Suite} suite @returns {string[]} */
function buildProblems(suite) {
  const errors = [];
  for (const key of /** @type {const} */ (["build", "msbuild"])) {
    const values = suite[key];
    if (values === undefined) {
      continue;
    }
    if (!uniqueStrings(values) || (key === "build" && !values.every(safeProject))) {
      errors.push(`Suite ${suite.id} has invalid ${key} inputs.`);
    }
  }
  return errors;
}

/** @param {import("./registry.mjs").Suite} suite @returns {string[]} */
function partitionProblems(suite) {
  if (suite.partitions === undefined) {
    return [];
  }
  if (!suite.partitions || typeof suite.partitions !== "object") {
    return [`Suite ${suite.id} has invalid partitions.`];
  }
  const { selector, ids } = suite.partitions;
  const valid =
    /^[A-Z][A-Z0-9_]*$/u.test(selector) &&
    uniqueStrings(ids) &&
    ids.every((id) => identifier.test(id));
  return valid ? [] : [`Suite ${suite.id} has invalid partitions.`];
}

/** @param {import("./registry.mjs").Suite} suite @returns {string[]} */
function environmentProblems(suite) {
  if (suite.env === undefined) {
    return [];
  }
  const { env } = suite;
  const shape = env !== null && typeof env === "object" && !Array.isArray(env);
  if (
    !shape ||
    !Object.entries(env).every(
      ([key, value]) =>
        /^[A-Z][A-Z0-9_]*$/u.test(key) && typeof value === "string" && !value.includes("\0"),
    )
  ) {
    return [`Suite ${suite.id} has invalid environment defaults.`];
  }
  return [];
}

/** @param {import("./registry.mjs").Suite} suite @returns {string[]} */
function dotnetProblems(suite) {
  if (suite.kind !== "dotnet") {
    return [];
  }
  const valid =
    safeProject(suite.project ?? "") &&
    /^[A-Za-z][A-Za-z0-9.]+$/u.test(suite.assembly ?? "") &&
    ["Release", "Debug"].includes(suite.configuration ?? "") &&
    /^[1-9][0-9]*(?:s|m|h)$/u.test(suite.timeout ?? "") &&
    typeof suite.coverage === "boolean";
  return valid ? [] : [`Suite ${suite.id} has invalid .NET metadata.`];
}

/** @param {import("./registry.mjs").Suite} suite @returns {string[]} */
export function problems(suite) {
  if (!suite || typeof suite !== "object") {
    return ["A registered suite must be an object."];
  }
  const errors = [];
  if (Object.keys(suite).some((key) => !fields.has(key))) {
    errors.push("A suite contains an unknown registry field.");
  }
  if (typeof suite.id !== "string" || !identifier.test(suite.id)) {
    errors.push("Suite IDs must be lowercase kebab-case.");
  }
  if (!kinds.has(suite.kind)) {
    errors.push(`Suite ${suite.id} has an unknown kind.`);
  }
  if (!uniqueStrings(suite.platforms) || !suite.platforms.every((os) => platforms.has(os))) {
    errors.push(`Suite ${suite.id} lists invalid or duplicate platforms.`);
  }
  if (suite.group !== undefined && !identifier.test(suite.group)) {
    errors.push(`Suite ${suite.id} has an invalid group.`);
  }
  return [
    ...errors,
    ...dotnetProblems(suite),
    ...buildProblems(suite),
    ...partitionProblems(suite),
    ...environmentProblems(suite),
  ];
}
