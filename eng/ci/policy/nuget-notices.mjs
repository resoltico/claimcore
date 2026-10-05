// Read redistribution material from the exact restored packages selected by the project graph.
import { existsSync, lstatSync, readFileSync, readdirSync } from "node:fs";
import { dirname, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const reviewedDirectory = resolve(dirname(fileURLToPath(import.meta.url)), "../../licenses");
const fallbacks = new Map([
  ["MIT", "MIT.txt"],
  ["PostgreSQL", "PostgreSQL-NPGSQL.txt"],
]);
const noticeName = /^(?:licen[cs]e|notice|third[-_]party[-_]notices)(?:\.(?:md|txt))?$/iu;

/** @typedef {import("./notices.mjs").Component} Component */
/** @typedef {{ type: string, path: string }} Library */
/** @typedef {{ libraries: Record<string, Library>, packageFolders: Record<string, object> }} Assets */

/** @param {string} path */
function textFile(path) {
  const stat = lstatSync(path);
  if (!stat.isFile() || stat.isSymbolicLink()) {
    throw new Error("Package redistribution material must be a regular file.");
  }
  const text = new TextDecoder("utf-8", { fatal: true }).decode(readFileSync(path)).trim();
  if (text === "") {
    throw new Error("Package redistribution material is empty.");
  }
  return text;
}

/** @param {Assets} assets @param {Component} component */
function packageDirectory(assets, { name = "", version = "" }) {
  const library = assets.libraries[`${name}/${version}`];
  if (library?.type !== "package" || !/^[a-z0-9_.-]+\/[a-z0-9_.+-]+$/iu.test(library.path)) {
    throw new Error("The SBOM package is absent from the restored graph.");
  }
  const directories = Object.keys(assets.packageFolders)
    .map((root) => ({ root: resolve(root), path: resolve(root, library.path) }))
    .filter(({ root, path }) => path.startsWith(root + sep) && existsSync(path));
  if (directories.length !== 1) {
    throw new Error("The restored package directory is missing or ambiguous.");
  }
  return directories[0]?.path ?? "";
}

/**
 * @param {string} assetsPath The evaluated ProjectAssetsFile, not an ambient cache guess.
 * @returns {(component: Component) => string}
 */
export function packageNoticeReader(assetsPath) {
  const assets = /** @type {Assets} */ (JSON.parse(readFileSync(assetsPath, "utf8")));
  if (!assets.libraries || !assets.packageFolders) {
    throw new Error("The restored project graph lacks package identity.");
  }
  return (component) => {
    const directory = packageDirectory(assets, component);
    const names = readdirSync(directory)
      .filter((name) => noticeName.test(name))
      .sort();
    const supplied = names.map((name) => `File: ${name}\n\n${textFile(resolve(directory, name))}`);
    if (!names.some((name) => /^licen[cs]e(?:\.(?:md|txt))?$/iu.test(name))) {
      const id = component.licenses?.[0]?.license?.id ?? "";
      const fallback = fallbacks.get(id);
      if (fallback === undefined) {
        throw new Error("The package lacks required license text.");
      }
      supplied.unshift(textFile(resolve(reviewedDirectory, fallback)));
    }
    return supplied.join("\n\n");
  };
}
