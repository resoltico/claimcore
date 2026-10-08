// Validate optional execution metadata before a plan can acquire tools or run commands.
const fields = new Set([
  "id",
  "argv",
  "after",
  "group",
  "exclusive",
  "env",
  "requires",
  "appendFiles",
]);

/** @param {unknown} value @returns {value is string[]} */
const strings = (value) =>
  Array.isArray(value) &&
  value.every((item) => typeof item === "string" && item.length > 0) &&
  new Set(value).size === value.length;

/** @param {string} value @returns {boolean} */
const relativeDirectory = (value) =>
  value === "." ||
  (!value.startsWith("/") &&
    !value.includes("\\") &&
    value.split("/").every((part) => part !== "" && part !== "." && part !== ".."));

/** @param {import("./types.mjs").Stage} stage */
function validateAppend(stage) {
  if (stage.appendFiles === undefined) {
    return;
  }
  const { directories, suffixes } = stage.appendFiles;
  if (
    !strings(directories) ||
    directories.length === 0 ||
    !directories.every(relativeDirectory) ||
    !strings(suffixes) ||
    suffixes.length === 0 ||
    !suffixes.every((suffix) => /^\.[a-z0-9]+$/u.test(suffix))
  ) {
    throw new Error(
      "Appended stage inputs need safe source directories and explicit file suffixes.",
    );
  }
}

/** @param {import("./types.mjs").Stage} stage */
function validateEnvironment(stage) {
  if (stage.env === undefined) {
    return;
  }
  const { env } = stage;
  if (env === null || typeof env !== "object" || Array.isArray(env)) {
    throw new Error("Stage environment must be a string-valued object.");
  }
  if (
    !Object.entries(env).every(
      ([key, value]) =>
        /^[A-Z][A-Z0-9_]*$/u.test(key) && typeof value === "string" && !value.includes("\0"),
    )
  ) {
    throw new Error("Stage environment contains an invalid name or value.");
  }
}

/** @param {import("./types.mjs").Stage} stage */
export function validateStageMetadata(stage) {
  if (!stage || typeof stage !== "object" || Object.keys(stage).some((key) => !fields.has(key))) {
    throw new Error("Stage metadata must contain only known execution fields.");
  }
  for (const key of /** @type {const} */ (["after", "requires"])) {
    if (stage[key] !== undefined && !strings(stage[key])) {
      throw new Error("Stage predecessors and requirements must be unique string lists.");
    }
  }
  if (stage.group !== undefined && (typeof stage.group !== "string" || stage.group.length === 0)) {
    throw new Error("Stage resource groups must be nonempty strings.");
  }
  if (stage.exclusive !== undefined && typeof stage.exclusive !== "boolean") {
    throw new Error("Stage exclusivity must be Boolean.");
  }
  validateEnvironment(stage);
  validateAppend(stage);
}
