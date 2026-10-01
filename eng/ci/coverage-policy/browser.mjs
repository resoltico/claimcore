// Fail unless a browser coverage report measured ClaimCore.Web production branches.
//
//   node eng/ci/coverage-policy/browser.mjs <cobertura-report>
import { checkBrowserCoverage } from "./policy.mjs";

const [report] = process.argv.slice(2);
try {
  if (report === undefined) {
    throw new Error("usage: browser.mjs <cobertura-report>");
  }
  checkBrowserCoverage(report);
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
}
