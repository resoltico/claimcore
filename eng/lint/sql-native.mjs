import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

/** @param {Record<string,string>} sources @param {string} [root] @returns {Record<string,unknown>} */
export function nativeSql(sources, root = fileURLToPath(new URL("../..", import.meta.url))) {
  const result = spawnSync("uv", ["run", "--frozen", "python", "-B", "eng/lint/sql_parser.py"], {
    cwd: root,
    input: JSON.stringify(sources),
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024,
    timeout: 60_000,
  });
  if (result.error || result.signal || result.status !== 0) {
    throw new Error("Native PostgreSQL syntax parsing was refused.");
  }
  const parsed = JSON.parse(result.stdout);
  if (
    JSON.stringify(parsed.version) !== "[18,6]" ||
    !parsed.sources ||
    typeof parsed.sources !== "object"
  ) {
    throw new Error("Native PostgreSQL parser version or output was refused.");
  }
  if (
    JSON.stringify(Object.keys(parsed.sources).sort()) !==
    JSON.stringify(Object.keys(sources).sort())
  ) {
    throw new Error("Native PostgreSQL parser omitted or added source.");
  }
  return parsed.sources;
}
