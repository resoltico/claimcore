// Command line: `main.mjs source` scans the repository; `main.mjs artifacts <path>...` scans uploads.
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { installTool } from "../tools.mjs";
import { scanArtifacts } from "./artifacts.mjs";
import { scanSource } from "./source.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const [mode, ...rest] = process.argv.slice(2);

if (mode === "source") {
  process.exitCode = await scanSource({ root, gitleaks: await installTool(root, "gitleaks") });
} else if (mode === "artifacts") {
  process.exitCode = await scanArtifacts(rest, { gitleaks: () => installTool(root, "gitleaks") });
} else {
  process.stderr.write("usage: main.mjs source | artifacts <path>...\n");
  process.exitCode = 2;
}
