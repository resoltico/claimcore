import { readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { repositoryFiles } from "../ci/repository.mjs";
import { nativeSql } from "./sql-native.mjs";
import { sqlFindings } from "./sql.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const { rules } = JSON.parse(readFileSync(join(root, "config/oxlint.json"), "utf8"));
const limits = {
  lines: rules["max-lines-per-function"][1].max,
  parameters: rules["max-params"][1].max,
  complexity: rules.complexity[1].max,
};
try {
  const files = repositoryFiles(root).filter((path) => path.endsWith(".sql"));
  if (!files.length) {
    throw new Error("No SQL source was admitted.");
  }
  const sources = Object.fromEntries(
    files.map((path) => [path, readFileSync(join(root, path), "utf8")]),
  );
  const trees = nativeSql(sources, root);
  const findings = files.flatMap((path) =>
    sqlFindings(readFileSync(join(root, path), "utf8"), trees[path], limits).map(
      (finding) => `${path}: ${finding}`,
    ),
  );
  findings.forEach((finding) => console.error(finding));
  if (findings.length) {
    process.exitCode = 1;
  } else {
    console.log(
      `Native PostgreSQL function limits passed for ${files.length} admitted source files.`,
    );
  }
} catch {
  console.error("Native PostgreSQL source limits were refused.");
  process.exitCode = 1;
}
