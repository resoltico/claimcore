import { readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { repositoryFiles } from "../ci/repository.mjs";
import { shellFindings } from "./shell.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const { rules } = JSON.parse(readFileSync(join(root, "config/oxlint.json"), "utf8"));
const limits = {
  lines: rules["max-lines-per-function"][1].max,
  complexity: rules.complexity[1].max,
  parameters: rules["max-params"][1].max,
};
const files = repositoryFiles(root).filter((path) => path.endsWith(".sh"));
try {
  const findings = files.flatMap((path) =>
    shellFindings(readFileSync(join(root, path), "utf8"), path, limits),
  );
  findings.forEach((finding) => console.error(finding));
  if (findings.length) {
    process.exitCode = 1;
  } else {
    console.log(`Native shell function limits passed for ${files.length} source files.`);
  }
} catch {
  console.error("Native shell source policy could not be completed.");
  process.exitCode = 1;
}
