// The GitHub Actions matrix of the cross-platform suites: every registered .NET suite outside a
// resource group, once per platform it supports, on a pinned runner image. Printed as the
// `matrix=<json>` line a workflow step appends to its outputs.
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { loadSuites } from "./registry.mjs";

export const runners = /** @type {const} */ ({
  linux: "ubuntu-24.04",
  macos: "macos-26",
  windows: "windows-2025-vs2026",
});

/**
 * @param {import("./registry.mjs").Suite[]} suites
 * @returns {{ include: { suite: string, platform: string, os: string }[] }}
 */
export function buildMatrix(suites) {
  const include = suites
    .filter((suite) => suite.kind === "dotnet" && suite.group === undefined)
    .flatMap((suite) =>
      suite.platforms.map((platform) => ({
        suite: suite.id,
        platform,
        os: runners[/** @type {keyof typeof runners} */ (platform)],
      })),
    );
  if (include.length === 0) {
    throw new Error("No registered suite runs across platforms.");
  }
  return { include };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
  process.stdout.write(`matrix=${JSON.stringify(buildMatrix(loadSuites(root)))}\n`);
  process.stdout.write(
    `tools=${JSON.stringify({ include: Object.entries(runners).map(([platform, os]) => ({ platform, os })) })}\n`,
  );
}
