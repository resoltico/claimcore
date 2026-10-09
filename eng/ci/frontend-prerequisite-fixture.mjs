import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { targets } from "../../web/scripts/check-mutation-report.mjs";
/** @param {string} source @param {string} relative @param {object} value */
export function writeJson(source, relative, value) {
  const path = join(source, relative);
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, JSON.stringify(value));
}
/** Independent synthetic owner-profile fixture. @param {string} source */
export function mutationReport(source) {
  return {
    schemaVersion: "1.0",
    framework: {
      name: "StrykerJS",
      version: JSON.parse(readFileSync(join(source, "web/package.json"), "utf8")).devDependencies[
        "@stryker-mutator/core"
      ],
    },
    thresholds: { high: 92, low: 92, break: 92 },
    config: {
      mutate: targets,
      testRunner: "vitest",
      vitest: { configFile: "vite.config.ts", related: true },
      ignorePatterns: ["artifacts/**"],
      coverageAnalysis: "perTest",
      mutator: { plugins: null, excludedMutations: [] },
      plugins: ["@stryker-mutator/*"],
      appendPlugins: [],
      ignorers: [],
      checkers: [],
      ignoreStatic: false,
      incremental: false,
      dryRunOnly: false,
      inPlace: false,
      testFiles: [],
      testRunnerNodeArgs: [],
    },
    files: Object.fromEntries(
      targets.map((target) => [
        target,
        {
          source: readFileSync(join(source, "web", target), "utf8"),
          mutants: [{ status: "Killed" }],
        },
      ]),
    ),
    testFiles: { "tests/synthetic.test.ts": {} },
  };
}
/** @param {string} source */
export function writePrerequisiteReports(source) {
  const names = readFileSync(join(source, "tests/inventory/vitest.txt"), "utf8").trim().split("\n");
  writeJson(source, "artifacts/frontend/vitest-summary.json", {
    format: "claimcore-vitest-report",
    formatVersion: 1,
    status: "passed",
    totals: { passed: names.length, failed: 0, skipped: 0, todo: 0 },
    tests: names.map((id) => ({ id, outcome: "passed", durationMs: 1 })),
  });
  const counts = { total: 1, covered: 1, skipped: 0, pct: 100 };
  const metrics = Object.fromEntries(
    ["branches", "functions", "lines", "statements"].map((name) => [name, counts]),
  );
  writeJson(source, "artifacts/frontend/coverage/coverage-summary.json", {
    total: metrics,
    [join(source, "web/src/domain/metadata.ts")]: metrics,
  });
  writeJson(source, "web/artifacts/stryker/domain-mutation.json", mutationReport(source));
}
