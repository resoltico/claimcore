// Verify one TRX report against a registered suite's inventory, for runs a script drives itself.
//
//   node eng/ci/suites/verify-report.mjs <suite-id> <trx-file>
import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseInventory } from "./inventory.mjs";
import { inventoryPath, loadSuites } from "./registry.mjs";
import { verifyTrx } from "./trx.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");

try {
  const [id, report] = process.argv.slice(2);
  const suite = loadSuites(root).find((candidate) => candidate.id === id);
  if (suite?.assembly === undefined || report === undefined) {
    throw new Error("usage: verify-report.mjs <suite-id> <trx-file>");
  }
  const names = parseInventory(readFileSync(join(root, inventoryPath(suite)), "utf8"));
  verifyTrx(readFileSync(report, "utf8"), { assembly: suite.assembly, names });
  process.stdout.write(`${suite.assembly} report matches its ${names.length} inventoried tests.\n`);
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
}
