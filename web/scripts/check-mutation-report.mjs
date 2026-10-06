import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

export const targets = [
  "src/domain/metadata.ts",
  "src/domain/operationInitial.ts",
  "src/domain/operationReducer.ts",
  "src/domain/operationRequest.ts",
  "src/presentation/preferences.ts",
  "src/presentation/presenter.ts",
  "src/presentation/messages.ts",
];
const minimumScore = 92;

/** @param {import("./tooling-types.mjs").MutationReport} report @param {string} version */
function requireIdentity(report, version) {
  if (
    report.schemaVersion !== "1.0" ||
    report.framework?.name !== "StrykerJS" ||
    report.framework.version !== version ||
    report.thresholds?.break !== minimumScore ||
    report.thresholds?.high !== minimumScore ||
    report.thresholds?.low !== minimumScore
  ) {
    throw new Error("Mutation evidence does not match the reviewed toolchain.");
  }
}

/** @param {import("./tooling-types.mjs").MutationReport} report @param {Record<string, string>} sources */
function requireScope(report, sources) {
  const files = Object.keys(report.files ?? {}).sort();
  if (
    JSON.stringify(files) !== JSON.stringify([...targets].sort()) ||
    targets.some((target) => report.files[target]?.source !== sources[target]) ||
    typeof report.testFiles !== "object" ||
    report.testFiles === null ||
    Object.keys(report.testFiles).length === 0
  ) {
    throw new Error("Mutation evidence does not match the reviewed targets.");
  }
}

/** @param {import("./tooling-types.mjs").MutationReport} report @param {Record<string, string>} sources @param {string} version */
export function verifyMutationReport(report, sources, version) {
  requireIdentity(report, version);
  requireScope(report, sources);
  const mutants = targets.flatMap((target) => report.files[target]?.mutants ?? []);
  if (
    mutants.length === 0 ||
    mutants.some((mutant) => !["Killed", "Survived"].includes(mutant.status))
  ) {
    throw new Error("Mutation evidence is empty, incomplete, or ignored.");
  }
  const killed = mutants.filter((mutant) => mutant.status === "Killed").length;
  const score = (100 * killed) / mutants.length;
  if (score < minimumScore) {
    throw new Error("Mutation score is below the reviewed floor.");
  }
  return { killed, total: mutants.length, score };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const config = JSON.parse(
    readFileSync(new URL("../stryker.config.json", import.meta.url), "utf8"),
  );
  if (
    JSON.stringify(config.mutate) !== JSON.stringify(targets) ||
    JSON.stringify(config.ignorePatterns) !== JSON.stringify(["artifacts/**"]) ||
    config.testRunner !== "vitest" ||
    config.vitest?.related !== true ||
    config.tempDirName !== "artifacts/stryker-tmp" ||
    config.cleanTempDir !== "always" ||
    config.thresholds?.break !== minimumScore
  ) {
    throw new Error("Mutation configuration is outside the reviewed scope.");
  }
  const packageJson = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8"));
  const version = packageJson.devDependencies?.["@stryker-mutator/core"];
  const sources = Object.fromEntries(
    targets.map((target) => [
      target,
      readFileSync(new URL(`../${target}`, import.meta.url), "utf8"),
    ]),
  );
  const report = JSON.parse(
    readFileSync(new URL("../artifacts/stryker/domain-mutation.json", import.meta.url), "utf8"),
  );
  const { killed, total, score } = verifyMutationReport(report, sources, version);
  process.stdout.write(
    `Mutation evidence passed: ${killed}/${total} killed (${score.toFixed(2)}%).\n`,
  );
}
