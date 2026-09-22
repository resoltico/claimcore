import { readdirSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

const categories = new Set(["expect", "fixture", "hook", "pw:api", "test.step"]);
const positive = (value) => Number.isSafeInteger(value) && value > 0;

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

const sourceLocation = (location, sources) => {
  const source = sources.get(location?.file);
  if (source === undefined || !positive(location.line) || location.line > source.lines) return null;
  if (!positive(location.column) || location.column > 10_000) return null;
  return { file: source.file, line: location.line, column: location.column };
};

// Step titles, selectors, URLs, expected/actual values and errors are never copied.
export class BrowserStepDiagnostic {
  latest = null;
  failed = null;

  constructor(sources) {
    this.sources = sources;
  }

  begin(step) {
    if (!categories.has(step.category)) return;
    const location = sourceLocation(step.location, this.sources);
    if (location !== null) this.latest = { category: step.category, ...location };
  }

  end(step) {
    if (this.failed !== null || step.error === undefined || !categories.has(step.category)) return;
    const location = sourceLocation(step.location, this.sources);
    if (location !== null) this.failed = { category: step.category, ...location };
  }

  snapshot() {
    return this.failed ?? this.latest;
  }
}
