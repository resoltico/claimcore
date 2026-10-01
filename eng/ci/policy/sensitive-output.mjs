// Fail when a diagnostic file contains a secret or any common encoding of it. A secret file's
// content is expanded to its base64, hex, URL-escaped and JSON-escaped forms (raw capability files
// are binary, so only their encoded forms can appear in text reports).
import { lstatSync, readdirSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const minimumLength = 8;
const secretLimit = 65_536;
const diagnosticLimit = 16_777_216;

/**
 * @param {string} path
 * @returns {Set<string>} The strings that must not appear in any diagnostic.
 */
export function secretVariants(path) {
  const stat = lstatSync(resolve(path));
  if (!stat.isFile()) {
    throw new Error("Sensitive-output scan input is not a regular secret file.");
  }
  if (stat.size > secretLimit) {
    throw new Error("Sensitive-output scan input exceeds its bounded size.");
  }
  const bytes = readFileSync(resolve(path));
  if (bytes.length < minimumLength) {
    throw new Error("Sensitive-output scan inputs must be at least eight characters.");
  }
  const variants = new Set([
    bytes.toString("base64"),
    bytes.toString("hex"),
    bytes.toString("hex").toUpperCase(),
  ]);
  let text;
  try {
    text = new TextDecoder("utf-8", { fatal: true }).decode(bytes).trim();
  } catch {
    return variants;
  }
  if (text.length < minimumLength) {
    throw new Error("Sensitive-output scan text inputs must be at least eight characters.");
  }
  const json = JSON.stringify(text);
  for (const variant of [
    text,
    Buffer.from(text, "utf8").toString("base64"),
    encodeURIComponent(text),
    json.slice(1, -1),
  ]) {
    variants.add(variant);
  }
  return variants;
}

/**
 * @param {string} directory
 * @returns {string[]} Every file below `directory`; links are refused.
 */
function filesBelow(directory) {
  const stat = lstatSync(directory);
  if (!stat.isDirectory()) {
    throw new Error("Sensitive-output scan root is not a regular directory.");
  }
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    const entry = lstatSync(path);
    if (entry.isSymbolicLink()) {
      throw new Error("Sensitive-output scan encountered a link.");
    }
    if (entry.isDirectory()) {
      return filesBelow(path);
    }
    if (entry.size > diagnosticLimit) {
      throw new Error("Sensitive-output scan encountered an oversized diagnostic file.");
    }
    return [path];
  });
}

/**
 * @param {string[]} scanRoots
 * @param {string[]} secretFiles
 * @returns {number} How many diagnostic files were scanned.
 */
export function assertNoSensitiveOutput(scanRoots, secretFiles) {
  const variants = secretFiles.flatMap((file) => [...secretVariants(file)]);
  const files = scanRoots.flatMap((root) => filesBelow(resolve(root)));
  for (const file of files) {
    const content = readFileSync(file, "utf8");
    if (variants.some((variant) => content.includes(variant))) {
      throw new Error("Sensitive-output scan rejected a diagnostic artifact.");
    }
  }
  return files.length;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const argv = process.argv.slice(2);
  /** @param {string} name */
  const values = (name) =>
    argv.flatMap((value, index) =>
      value === name && argv[index + 1] !== undefined
        ? [/** @type {string} */ (argv[index + 1])]
        : [],
    );
  try {
    const count = assertNoSensitiveOutput(values("--scan-root"), values("--secret-file"));
    process.stdout.write(`Sensitive-output scan passed for ${count} diagnostic files.\n`);
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  }
}
