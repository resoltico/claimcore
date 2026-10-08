import { createHash } from "node:crypto";
import { lstatSync, readFileSync, readdirSync, realpathSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseDocument } from "yaml";

/** @param {string} path */
function physicalFile(path) {
  if (!lstatSync(path).isFile() || realpathSync(path) !== resolve(path)) {
    throw new Error("Fresh baseline source input is not a physical regular file.");
  }
  return readFileSync(path);
}

/** @param {unknown} paths @returns {string[]} */
function fragments(paths) {
  if (
    !Array.isArray(paths) ||
    !paths.length ||
    paths.some((path) => typeof path !== "string" || !/^[a-z][a-z0-9-]*\.sql$/u.test(path)) ||
    new Set(paths).size !== paths.length
  ) {
    throw new Error("Fresh baseline source fragment names are invalid.");
  }
  return paths;
}

/** @param {string} path */
function manifest(path) {
  const source = physicalFile(path).toString("utf8");
  const value = JSON.parse(source);
  if (parseDocument(source, { uniqueKeys: true }).errors.length) {
    throw new Error("Fresh baseline source has ambiguous properties.");
  }
  if (
    JSON.stringify(Object.keys(value).sort()) !==
      JSON.stringify(["baselineId", "fragments", "schemaVersion", "sha256"]) ||
    value.schemaVersion !== 2 ||
    !/^[a-z][a-z0-9-]{0,63}$/u.test(value.baselineId) ||
    !/^[0-9a-f]{64}$/u.test(value.sha256)
  ) {
    throw new Error("Fresh baseline source manifest is invalid.");
  }
  return { sha256: String(value.sha256), fragments: fragments(value.fragments) };
}

/** Assemble one fresh baseline from its reviewed ordered source fragments. @param {string} manifestPath */
export function baselineSource(manifestPath) {
  const admitted = manifest(manifestPath);
  const sourceName = basename(manifestPath, ".json");
  const directory = join(
    dirname(manifestPath),
    sourceName === "schema-baseline" ? "baseline" : sourceName,
  );
  if (!lstatSync(directory).isDirectory() || realpathSync(directory) !== resolve(directory)) {
    throw new Error("Fresh baseline source directory is not physical.");
  }
  if (
    JSON.stringify(readdirSync(directory).sort()) !== JSON.stringify([...admitted.fragments].sort())
  ) {
    throw new Error("Fresh baseline source inventory differs from its manifest.");
  }
  const bytes = Buffer.concat(
    admitted.fragments.map((path) => physicalFile(join(directory, path))),
  );
  if (createHash("sha256").update(bytes).digest("hex") !== admitted.sha256) {
    throw new Error("Fresh baseline source bytes differ from the checksum.");
  }
  return bytes;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const [path] = process.argv.slice(2);
    if (!path || process.argv.length !== 3) {
      throw new Error("A baseline source manifest is required.");
    }
    process.stdout.write(baselineSource(resolve(path)));
  } catch {
    console.error("Fresh baseline source assembly was refused.");
    process.exitCode = 1;
  }
}
