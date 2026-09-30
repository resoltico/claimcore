// The locked dependency graph as the repository actually pins it: NuGet lock files and npm lock files.
import { createHash } from "node:crypto";
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { packageKey } from "./dependency-policy.mjs";

const skipped = new Set(["bin", "obj", "node_modules", "artifacts", ".git"]);

/** Resolved versions per `ecosystem|name` key. @typedef {Map<string, Set<string>>} Installed */

/**
 * @param {Installed} installed
 * @param {string} ecosystem
 * @param {string} name
 * @param {string} resolved
 */
function add(installed, ecosystem, name, resolved) {
  const key = packageKey(ecosystem, name);
  const versions = installed.get(key) ?? new Set();
  versions.add(resolved);
  installed.set(key, versions);
}

/**
 * Every NuGet `packages.lock.json` below `directory`.
 * @param {string} directory
 * @param {Installed} installed
 */
export function readNuGetLocks(directory, installed) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && !skipped.has(entry.name)) {
      readNuGetLocks(join(directory, entry.name), installed);
    }
    if (entry.isFile() && entry.name === "packages.lock.json") {
      addNuGetLock(JSON.parse(readFileSync(join(directory, entry.name), "utf8")), installed);
    }
  }
}

/**
 * The resolved packages of every target framework in one parsed lock file.
 * @param {{ dependencies: Record<string, Record<string, { resolved?: string }>> }} lock
 * @param {Installed} installed
 */
function addNuGetLock(lock, installed) {
  for (const framework of Object.values(lock.dependencies)) {
    for (const [name, value] of Object.entries(framework)) {
      if (value.resolved) {
        add(installed, "nuget", name, value.resolved);
      }
    }
  }
}

/**
 * One npm project's lock file.
 * @param {string} path Path to package-lock.json.
 * @param {Installed} installed
 * @returns {string} The lock file's SHA-256.
 */
export function readNpmLock(path, installed) {
  const bytes = readFileSync(path);
  const lock = JSON.parse(bytes.toString("utf8"));
  for (const [key, value] of Object.entries(
    /** @type {Record<string, { version?: string }>} */ (lock.packages),
  )) {
    if (key && value.version) {
      add(installed, "npm", key.split("node_modules/").at(-1) ?? key, value.version);
    }
  }
  return createHash("sha256").update(bytes).digest("hex");
}
