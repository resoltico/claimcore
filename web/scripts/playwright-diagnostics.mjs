import { readdirSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

const categories = new Set(["expect", "fixture", "hook", "pw:api", "test.step"]);
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

// Step titles, selectors, URLs, expected/actual values and errors are never copied.
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
      this.failed = { category: step.category, ...location };
    }
  }

  snapshot() {
    return this.failed ?? this.latest;
  }
}
