import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

const target = "src/domain/operationReducer.ts";
const minimumScore = 92;

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

function requireScope(report, source) {
  if (
    Object.keys(report.files ?? {}).length !== 1 ||
    !Object.hasOwn(report.files, target) ||
    report.files[target].source !== source ||
    typeof report.testFiles !== "object" ||
    report.testFiles === null ||
    Object.keys(report.testFiles).length === 0
  ) {
    throw new Error("Mutation evidence does not match the reviewed target.");
  }
}

export function verifyMutationReport(report, source, version) {
  requireIdentity(report, version);
  requireScope(report, source);
  const { mutants } = report.files[target];
  if (
    !Array.isArray(mutants) ||
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
  const config = JSON.parse(readFileSync(new URL("../stryker.config.json", import.meta.url)));
  if (
    JSON.stringify(config.mutate) !== JSON.stringify([target]) ||
    JSON.stringify(config.ignorePatterns) !== JSON.stringify(["artifacts/**"]) ||
    config.testRunner !== "vitest" ||
    config.vitest?.related !== true ||
    config.tempDirName !== "artifacts/stryker-tmp" ||
    config.cleanTempDir !== "always" ||
    config.thresholds?.break !== minimumScore
  ) {
    throw new Error("Mutation configuration is outside the reviewed scope.");
  }
  const packageJson = JSON.parse(readFileSync(new URL("../package.json", import.meta.url)));
  const version = packageJson.devDependencies?.["@stryker-mutator/core"];
  const source = readFileSync(new URL(`../${target}`, import.meta.url), "utf8");
  const report = JSON.parse(
    readFileSync(new URL("../artifacts/stryker/operation-reducer.json", import.meta.url)),
  );
  const { killed, total, score } = verifyMutationReport(report, source, version);
  process.stdout.write(
    `Mutation evidence passed: ${killed}/${total} killed (${score.toFixed(2)}%).\n`,
  );
}
