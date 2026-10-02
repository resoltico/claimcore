// Parse .NET suite selection before restoring, building or starting any test process.

const valued = new Set(["group", "platform", "parallel", "results-root"]);
const flags = new Set(["build", "cross-platform"]);

/** @param {Record<string, string | undefined>} environment */
export function requireCompletePropertyProfile(environment) {
  if (
    environment["CLAIMCORE_PROPERTY_PROFILE"] === "recheck" ||
    environment["CLAIMCORE_PROPERTY_RECHECK_ID"] ||
    environment["CLAIMCORE_PROPERTY_RECHECK_TOKEN"]
  ) {
    throw new Error(
      "Diagnostic property rechecks cannot provide complete suite evidence. Run the native test executable directly for diagnosis.",
    );
  }
}

/** @param {string} name @param {string | undefined} value @param {Record<string, string>} options @param {Set<string>} enabled */
function readOption(name, value, options, enabled) {
  if (options[name] !== undefined || enabled.has(name)) {
    throw new Error(`Repeated suite option: ${name}.`);
  }
  if (flags.has(name)) {
    enabled.add(name);
    return 0;
  }
  if (valued.has(name) && value && !value.startsWith("--")) {
    options[name] = value;
    return 1;
  }
  throw new Error(`Unknown suite option or missing value: ${name}.`);
}

/** @param {string[]} ids @param {Record<string, string>} options @param {Set<string>} enabled */
function checkModes(ids, options, enabled) {
  const modes =
    Number(ids.length > 0) +
    Number(options["group"] !== undefined) +
    Number(enabled.has("cross-platform"));
  if (new Set(ids).size !== ids.length || modes !== 1) {
    throw new Error("Select unique suite IDs, one group, or cross-platform suites.");
  }
}

/** @param {string[]} argv */
export function parseSelection(argv) {
  if (argv[0] !== "run") {
    throw new Error("Use suite.mjs run with registered .NET suites.");
  }
  /** @type {string[]} */
  const ids = [];
  /** @type {Record<string, string>} */
  const options = {};
  const enabled = new Set();
  for (let index = 1; index < argv.length; index += 1) {
    const part = argv[index] ?? "";
    if (part.startsWith("--")) {
      index += readOption(part.slice(2), argv[index + 1], options, enabled);
    } else {
      ids.push(part);
    }
  }
  checkModes(ids, options, enabled);
  return { ids, options, enabled };
}

/**
 * @param {import("./registry.mjs").Suite[]} suites
 * @param {ReturnType<typeof parseSelection>} selection
 * @param {string} platform
 * @returns {import("./registry.mjs").Suite[]}
 */
export function selectSuites(suites, { ids, options, enabled }, platform) {
  if (!["linux", "macos", "windows"].includes(platform)) {
    throw new Error("Unknown suite platform.");
  }
  let selected = suites.filter((suite) => ids.includes(suite.id));
  if (enabled.has("cross-platform")) {
    selected = suites.filter(
      (suite) =>
        suite.kind === "dotnet" && suite.group === undefined && suite.platforms.includes(platform),
    );
  } else if (options["group"]) {
    selected = suites.filter((suite) => suite.group === options["group"]);
  }
  if (selected.length === 0 || ids.some((id) => !selected.some((suite) => suite.id === id))) {
    throw new Error("Name registered suites or a nonempty registered group.");
  }
  if (selected.some((suite) => suite.kind !== "dotnet")) {
    throw new Error("The .NET runner accepts only .NET suites; use the frontend adapter.");
  }
  return selected;
}
