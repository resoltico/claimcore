import { checkCliCoverage } from "./policy.mjs";

try {
  const [report] = process.argv.slice(2);
  if (report === undefined) {
    throw new Error("usage: cli.mjs <cobertura-report>");
  }
  checkCliCoverage(report);
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
}
