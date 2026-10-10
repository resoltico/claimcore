import { readdirSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

const categories = new Set(["expect", "fixture", "hook", "pw:api", "test.step"]);
/** @typedef {"response-body-unavailable"|"json-parse"|"cancelled-or-closed"|"timeout"|"assertion"|"unknown"} BrowserFailureKind */

/** Classify bounded engine messages without returning any message, selector, URL or value.
 * @param {unknown} error @returns {BrowserFailureKind} */
export const browserFailureKind = (error) => {
  const message =
    typeof error === "object" &&
    error !== null &&
    "message" in error &&
    typeof error.message === "string"
      ? error.message.slice(0, 8192)
      : "";
  if (
    /Network\.getResponseBody.*(?:No resource with given identifier|No data found for resource|evicted from inspector cache)|Response body is unavailable/iu.test(
      message,
    )
  ) {
    return "response-body-unavailable";
  }
  if (
    /Unexpected end of JSON input|is not valid JSON|in JSON at position|JSON\.parse:/iu.test(
      message,
    )
  ) {
    return "json-parse";
  }
  if (
    /Target (?:page, context or browser has been closed|closed)|AbortError|net::ERR_ABORTED|NS_BINDING_ABORTED/iu.test(
      message,
    )
  ) {
    return "cancelled-or-closed";
  }
  if (/Timeout \d+ms exceeded|Test timeout of \d+ms exceeded|Timed out waiting/iu.test(message)) {
    return "timeout";
  }
  if (/\bexpect\(|\bAssertionError\b/u.test(message)) {
    return "assertion";
  }
  return "unknown";
};
/** @param {unknown} value @returns {value is number} */
const positive = (value) => typeof value === "number" && Number.isSafeInteger(value) && value > 0;

/**
 * @typedef {object} Source
 * @property {string} file Repository-relative path.
 * @property {number} lines
 */

/**
 * @typedef {object} StepLocation
 * @property {string} file
 * @property {number} line
 * @property {number} column
 */

/**
 * @typedef {object} Step
 * @property {string} category
 * @property {StepLocation} [location]
 * @property {unknown} [error]
 */

/**
 * @typedef {object} Located
 * @property {string} category
 * @property {string} file
 * @property {number} line
 * @property {number} column
 * @property {BrowserFailureKind} [errorKind]
 */

/**
 * The browser specifications by absolute path, with their line counts.
 * @param {string} root
 * @returns {Map<string, Source>}
 */
export const browserSources = (root) => {
  const directory = resolve(root, "web/e2e");
  return new Map(
    readdirSync(directory, { withFileTypes: true })
      .filter((entry) => entry.isFile() && /^[a-z0-9-]+\.(?:spec\.)?ts$/u.test(entry.name))
      .map((entry) => {
        const path = resolve(directory, entry.name);
        return [
          path,
          { file: `web/e2e/${entry.name}`, lines: readFileSync(path, "utf8").split("\n").length },
        ];
      }),
  );
};

/**
 * @param {StepLocation | undefined} location
 * @param {Map<string, Source>} sources
 * @returns {StepLocation | null}
 */
const sourceLocation = (location, sources) => {
  const source = sources.get(location?.file ?? "");
  if (location === undefined || source === undefined) {
    return null;
  }
  if (!positive(location.line) || location.line > source.lines) {
    return null;
  }
  if (!positive(location.column) || location.column > 10_000) {
    return null;
  }
  return { file: source.file, line: location.line, column: location.column };
};

// Step titles, selectors, URLs, expected/actual values and error messages are never copied.
export class BrowserStepDiagnostic {
  /** @type {Located | null} */
  latest = null;
  /** @type {Located | null} */
  failed = null;

  /** @param {Map<string, Source>} sources */
  constructor(sources) {
    this.sources = sources;
  }

  /** @param {Step} step */
  begin(step) {
    if (!categories.has(step.category)) {
      return;
    }
    const location = sourceLocation(step.location, this.sources);
    if (location !== null) {
      this.latest = { category: step.category, ...location };
    }
  }

  /** @param {Step} step */
  end(step) {
    if (this.failed !== null || step.error === undefined || !categories.has(step.category)) {
      return;
    }
    const location = sourceLocation(step.location, this.sources);
    if (location !== null) {
      this.failed = {
        category: step.category,
        ...location,
        errorKind: browserFailureKind(step.error),
      };
    }
  }

  snapshot() {
    return this.failed ?? this.latest;
  }
}

/** @param {unknown} value @param {string[]} keys @returns {value is Record<string, unknown>} */
const exactKeys = (value, keys) =>
  typeof value === "object" &&
  value !== null &&
  !Array.isArray(value) &&
  Object.keys(value).sort().join(",") === keys.sort().join(",");
/** @param {unknown} value @param {string[]} choices */
const oneOf = (value, choices) => choices.some((choice) => choice === value);
/** @param {unknown} value @param {number} min @param {number} max */
const boundedInteger = (value, min, max) =>
  typeof value === "number" && Number.isInteger(value) && value >= min && value <= max;

/** @param {unknown} value */
const requestEvidence = (value) =>
  exactKeys(value, ["kind", "status", "phase", "elapsedMs"]) &&
  oneOf(value["kind"], ["document", "script", "stylesheet", "session", "definition"]) &&
  oneOf(value["phase"], ["pending", "headers", "finished", "failed"]) &&
  (value["status"] === null || boundedInteger(value["status"], 100, 599)) &&
  boundedInteger(value["elapsedMs"], 0, 60_000);
/** @param {unknown} value */
const readinessEvidence = (value) =>
  value === null ||
  (exactKeys(value, ["readyState", "view", "alert"]) &&
    oneOf(value["readyState"], ["loading", "interactive", "complete"]) &&
    oneOf(value["view"], ["loading", "login-shell", "app-shell", "other"]) &&
    typeof value["alert"] === "boolean");
/** @param {unknown} value @returns {value is Record<string, unknown>} */
const startupHeader = (value) =>
  exactKeys(value, ["authenticated", "pageErrorSeen", "readiness", "requests", "truncated"]) &&
  typeof value["truncated"] === "boolean" &&
  typeof value["pageErrorSeen"] === "boolean" &&
  (value["authenticated"] === null || typeof value["authenticated"] === "boolean");

/** Closed startup evidence only; never admit arbitrary Playwright attachment content.
 * @param {Buffer | undefined} body
 * @returns {unknown | null}
 */
export const startupDiagnostic = (body) => {
  if (body === undefined || body.length > 16_384) {
    return null;
  }
  try {
    const value = JSON.parse(body.toString("utf8"));
    if (!startupHeader(value) || !readinessEvidence(value["readiness"])) {
      return null;
    }
    const { requests } = value;
    if (!Array.isArray(requests) || requests.length > 40 || !requests.every(requestEvidence)) {
      return null;
    }
    return value;
  } catch {
    return null;
  }
};
