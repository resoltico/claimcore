import { lstatSync, mkdirSync, readdirSync, writeFileSync, chownSync } from "node:fs";
import { join } from "node:path";

export const runtimeRoot = "/etc/claimcore/runtime";

/** @param {string} path @param {number} uid @param {number} gid */
export function privateDirectory(path, uid, gid) {
  mkdirSync(path, { mode: 0o700 });
  chownSync(path, uid, gid);
}

/** @param {string} path @param {string | Uint8Array} content @param {number} uid @param {number} gid */
export function privateFile(path, content, uid, gid) {
  writeFileSync(path, content, { mode: 0o600, flag: "wx" });
  chownSync(path, uid, gid);
}

/** @param {string} directory @param {number} uid */
export function emptyRoot(directory, uid) {
  const info = lstatSync(directory);
  if (
    !info.isDirectory() ||
    info.isSymbolicLink() ||
    info.uid !== uid ||
    (info.mode & 0o077) !== 0 ||
    readdirSync(directory).length !== 0
  ) {
    throw new Error(
      "Configuration creation requires an empty physical directory; existing state is preserved.",
    );
  }
}

/** @param {string} directory @param {string} name @param {unknown} value @param {number} uid @param {number} gid */
export function jsonFile(directory, name, value, uid, gid) {
  privateFile(join(directory, name), `${JSON.stringify(value, null, 2)}\n`, uid, gid);
}
