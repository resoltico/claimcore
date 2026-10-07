// A published tree is described by a manifest: every regular file's path, length and SHA-256, and a
// digest over those records. Consumers verify the tree against it before and after they use it, so
// bytes that were published once are the bytes that were exercised.
import { createHash } from "node:crypto";
import { lstatSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { compareOrdinal } from "../suites/inventory.mjs";

/** @typedef {{ path: string, length: number, sha256: string }} PublishedFile */
/** @typedef {{ schemaVersion: 2, product: string, files: PublishedFile[], treeSha256: string, producingInputs: Record<string, string> }} Manifest */

const digestPattern = /^[0-9a-f]{64}$/u;

/** @param {Uint8Array | string} data */
const sha256 = (data) => createHash("sha256").update(data).digest("hex");

/**
 * @param {string} directory
 * @returns {string[]} Absolute paths of every regular file, links refused.
 */
function walk(directory) {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    const stat = lstatSync(path);
    if (stat.isSymbolicLink()) {
      throw new Error("Output trees may not contain symbolic links or junctions.");
    }
    if (stat.isDirectory()) {
      return walk(path);
    }
    if (!stat.isFile()) {
      throw new Error("Output trees may contain only regular files.");
    }
    return [path];
  });
}

/**
 * @param {string} root
 * @returns {PublishedFile[]} The tree's files, sorted by ordinal path.
 */
export function describeFiles(root) {
  if (!lstatSync(root).isDirectory()) {
    throw new Error("The output root is not a directory.");
  }
  return walk(root)
    .map((path) => {
      const content = readFileSync(path);
      return {
        path: relative(root, path).split(sep).join("/"),
        length: content.length,
        sha256: sha256(content),
      };
    })
    .sort((left, right) => compareOrdinal(left.path, right.path));
}

/**
 * @param {PublishedFile[]} files Sorted by ordinal path.
 * @returns {string}
 */
export const treeDigest = (files) =>
  sha256(files.map((file) => `${file.path}\0${file.length}\0${file.sha256}\n`).join(""));

/**
 * @param {string} product
 * @param {string} root
 * @param {Record<string, string>} producingInputs
 * @returns {Manifest}
 */
export function createManifest(product, root, producingInputs) {
  const files = describeFiles(root);
  if (files.length === 0) {
    throw new Error("A published tree cannot be empty.");
  }
  return { schemaVersion: 2, product, files, treeSha256: treeDigest(files), producingInputs };
}

/**
 * @param {string} product
 * @param {string} root
 * @param {string} manifestPath
 * @param {Record<string, string>} producingInputs
 */
export function writeManifest(product, root, manifestPath, producingInputs) {
  writeFileSync(
    manifestPath,
    `${JSON.stringify(createManifest(product, root, producingInputs), null, 2)}\n`,
    {
      flag: "wx",
    },
  );
}

/**
 * @param {string} path
 * @returns {boolean}
 */
const safePath = (path) =>
  path.trim() !== "" &&
  !path.includes("\\") &&
  !path.includes("\0") &&
  !path.startsWith("/") &&
  !/^[A-Za-z]:/u.test(path) &&
  path.split("/").every((part) => part !== "" && part !== "." && part !== "..");

/**
 * @param {PublishedFile} file
 * @returns {boolean}
 */
function validFileRecord(file) {
  return (
    JSON.stringify(Object.keys(file ?? {}).sort()) ===
      JSON.stringify(["length", "path", "sha256"]) &&
    safePath(file.path) &&
    Number.isSafeInteger(file.length) &&
    file.length >= 0 &&
    digestPattern.test(file.sha256)
  );
}

/** @param {Record<string, string>} inputs */
function validProducingInputs(inputs) {
  return (
    inputs !== null &&
    typeof inputs === "object" &&
    !Array.isArray(inputs) &&
    Object.keys(inputs).length > 0 &&
    Object.entries(inputs).every(([path, digest]) => safePath(path) && digestPattern.test(digest))
  );
}

/**
 * @param {unknown} value
 * @returns {Manifest}
 */
function checkedManifest(value) {
  const manifest = /** @type {Manifest} */ (value);
  const keys = Object.keys(manifest ?? {}).sort();
  if (
    JSON.stringify(keys) !==
    JSON.stringify(["files", "product", "producingInputs", "schemaVersion", "treeSha256"].sort())
  ) {
    throw new Error("The publish manifest has missing or unknown properties.");
  }
  if (
    manifest.schemaVersion !== 2 ||
    !Array.isArray(manifest.files) ||
    manifest.files.length === 0 ||
    !digestPattern.test(manifest.treeSha256)
  ) {
    throw new Error("The publish manifest is malformed.");
  }
  if (!validProducingInputs(manifest.producingInputs)) {
    throw new Error("The publish manifest has invalid producing inputs.");
  }
  if (!manifest.files.every(validFileRecord)) {
    throw new Error("The publish manifest has an invalid file record.");
  }
  return manifest;
}

/**
 * Require the tree at `root` to be exactly the one `manifestPath` describes.
 * @param {string} product
 * @param {string} root
 * @param {string} manifestPath
 * @returns {string} The tree digest.
 */
export function verifyTree(product, root, manifestPath) {
  const manifest = checkedManifest(JSON.parse(readFileSync(manifestPath, "utf8")));
  if (manifest.product !== product) {
    throw new Error("The publish manifest names a different product.");
  }
  const declared = manifest.files.map((file) => file.path);
  if (
    JSON.stringify(declared) !== JSON.stringify([...declared].sort(compareOrdinal)) ||
    new Set(declared).size !== declared.length
  ) {
    throw new Error("The publish manifest files must be uniquely sorted by ordinal path.");
  }
  if (treeDigest(manifest.files) !== manifest.treeSha256) {
    throw new Error("The publish manifest tree digest does not match its file records.");
  }
  const actual = describeFiles(root);
  if (JSON.stringify(actual) !== JSON.stringify(manifest.files)) {
    throw new Error("The output tree does not match the publish manifest.");
  }
  return manifest.treeSha256;
}
