import { executable } from "../executable.mjs";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { lstatSync, readdirSync, readFileSync } from "node:fs";
import { extname, join, resolve } from "node:path";
import { resolveSourceFile } from "../repository-path.mjs";

const root = resolve(import.meta.dirname, "../../..");
/** @param {string} directory @returns {string[]} */
export function producingInputFiles(directory) {
  const spec = JSON.parse(
    readFileSync(resolveSourceFile(directory, "config/publication-inputs.json"), "utf8"),
  );
  const files = new Set(spec.files);
  /** @param {string} path */
  const excluded = (path) =>
    spec.excludedDirectories.some(
      (/** @type {string} */ item) =>
        path === item || path.startsWith(`${item}/`) || path.split("/").includes(item),
    );
  /** @param {string} path */
  const walk = (path) => {
    if (lstatSync(join(directory, path)).isSymbolicLink()) {
      throw new Error("Linked publication inputs are refused.");
    }
    for (const item of readdirSync(join(directory, path))) {
      const name = `${path}/${item}`;
      if (excluded(name)) {
        continue;
      }
      const stat = lstatSync(join(directory, name));
      if (stat.isSymbolicLink()) {
        throw new Error("Linked publication inputs are refused.");
      }
      if (stat.isDirectory()) {
        walk(name);
      } else if (stat.isFile() && spec.extensions.includes(extname(name))) {
        files.add(name);
      }
    }
  };
  spec.directories.forEach(walk);
  const paths = [...files].sort();
  paths.forEach((path) => resolveSourceFile(directory, path));
  return paths;
}

/** @param {string} directory @returns {string} */
export function producingInputDigest(directory) {
  const hash = createHash("sha256");
  for (const path of producingInputFiles(directory)) {
    const digest = createHash("sha256")
      .update(readFileSync(resolveSourceFile(directory, path)))
      .digest("hex");
    hash.update(`${path}\0${digest}\n`);
  }
  return hash.digest("hex");
}

/** @param {string} directory @returns {Record<string, string>} */
export function compiledInputs(directory) {
  const result = execFileSync(
    executable("dotnet"),
    ["fsi", "--exec", join(root, "eng/PublicationInputs.fsx"), "read", directory],
    {
      cwd: root,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    },
  );
  return JSON.parse(result);
}

/** @param {Record<string, string>} values @param {string} expected */
export function verifyProducingInputs(values, expected) {
  if (
    Object.keys(values).length === 0 ||
    Object.values(values).some((value) => value !== expected)
  ) {
    throw new Error(
      "Published assemblies were produced from different inputs; rebuild before qualification.",
    );
  }
}
