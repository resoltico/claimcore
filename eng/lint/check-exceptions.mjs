// Fails when a suppression exists outside config/lint-exceptions.json, when an exception has no
// suppression left, or when a rule that no exception may relax is broken.
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { checkRepository } from "./engine.mjs";

const root = resolve(fileURLToPath(new URL("../..", import.meta.url)));
const { report, registered, active } = checkRepository(root);

if (report.errors.length > 0) {
  for (const message of report.errors) console.error(message);
  console.error(`${report.errors.length} lint exception finding(s).`);
  process.exitCode = 1;
} else {
  console.log(`Lint exception registry is valid: ${registered} registered, ${active} active.`);
}
