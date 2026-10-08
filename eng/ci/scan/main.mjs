// Command line: `main.mjs source` scans the repository; `main.mjs artifacts <path>...` scans uploads.
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { installTool } from "../tools.mjs";
import { scanArtifacts } from "./artifacts.mjs";
import { scanSource } from "./source.mjs";
import { historyRoot } from "../run-context.mjs";
import { scanHistory } from "./history.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const [mode, ...rest] = process.argv.slice(2);

try {
  if (mode === "source") {
    process.exitCode = await scanSource({ root, gitleaks: await installTool(root, "gitleaks") });
  } else if (mode === "history") {
    process.exitCode = await scanHistory({
      root: await historyRoot(root),
      gitleaks: await installTool(root, "gitleaks"),
    });
  } else if (mode === "artifacts") {
    process.exitCode = await scanArtifacts(rest, { gitleaks: () => installTool(root, "gitleaks") });
  } else {
    process.stderr.write("usage: main.mjs source | history | artifacts <path>...\n");
    process.exitCode = 2;
  }
} catch {
  process.stderr.write(
    "Secret qualification could not admit inputs or acquire the pinned scanner.\n",
  );
  process.exitCode = 2;
}
