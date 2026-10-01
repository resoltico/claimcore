// The contract lock: config/contracts.lock.json records the byte length and SHA-256 of every
// generated contract artifact. The artifacts themselves are generated, never tracked; the lock is
// what a reviewer reads when a contract changes, and what every consumer verifies before use.
import { createHash } from "node:crypto";
import { readdir, readFile, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";

const root = resolve(import.meta.dirname, "../..");
const lockPath = resolve(root, "config/contracts.lock.json");
export const generatedDirectory = resolve(root, "web/src/generated/contracts");

/** @param {string} left @param {string} right */
const compareOrdinal = (left, right) => Buffer.compare(Buffer.from(left), Buffer.from(right));

/** @param {import("./tooling-types.mjs").ContractFile} file */
const validFile = (file) =>
  file !== null &&
  typeof file === "object" &&
  Object.keys(file).length === 3 &&
  typeof file.path === "string" &&
  /^[A-Za-z0-9][A-Za-z0-9._-]*$/u.test(file.path) &&
  Number.isSafeInteger(file.bytes) &&
  file.bytes >= 0 &&
  typeof file.sha256 === "string" &&
  /^[0-9a-f]{64}$/u.test(file.sha256);

/** @param {import("./tooling-types.mjs").ContractFile[]} files */
const validateFiles = (files) => {
  if (!Array.isArray(files) || files.length === 0 || !files.every(validFile)) {
    throw new Error("The contract lock requires a nonempty valid file list.");
  }
  const portable = new Set();
  for (const [index, file] of files.entries()) {
    if (
      portable.has(file.path.toLowerCase()) ||
      (index > 0 && compareOrdinal(files[index - 1]?.path ?? "", file.path) >= 0)
    ) {
      throw new Error("The contract lock file list must be unique, portable and in ordinal order.");
    }
    portable.add(file.path.toLowerCase());
  }
};

/**
 * @param {string} directory
 * @returns {Promise<{ path: string, bytes: number, sha256: string }[]>}
 */
export const describeDirectory = async (directory) => {
  const entries = await readdir(directory, { withFileTypes: true });
  if (entries.some((entry) => !entry.isFile())) {
    throw new Error("Generated contract output must contain regular files only.");
  }
  const described = await Promise.all(
    entries.map(async (entry) => {
      const content = await readFile(join(directory, entry.name));
      return {
        path: entry.name,
        bytes: content.byteLength,
        sha256: createHash("sha256").update(content).digest("hex"),
      };
    }),
  );
  return described.sort((left, right) => compareOrdinal(left.path, right.path));
};

/** @param {string} directory @param {string} [lockFile] */
export const writeLock = async (directory, lockFile = lockPath) => {
  const files = await describeDirectory(directory);
  validateFiles(files);
  await writeFile(lockFile, `${JSON.stringify({ schemaVersion: 1, files }, null, 2)}\n`);
};

/**
 * Require the directory to hold exactly the artifacts the lock names, byte for byte.
 * @param {string} directory
 * @param {string} [lockFile] The lock file; the committed lock by default.
 */
export const verifyLock = async (directory, lockFile = lockPath) => {
  const lock =
    /** @type {{schemaVersion: number, files: import("./tooling-types.mjs").ContractFile[]}} */ (
      JSON.parse(await readFile(lockFile, "utf8"))
    );
  if (!lock || lock.schemaVersion !== 1 || Object.keys(lock).length !== 2) {
    throw new Error("config/contracts.lock.json is malformed.");
  }
  validateFiles(lock.files);
  const actual = await describeDirectory(directory).catch(() => {
    throw new Error(
      "Generated contracts are absent; run `npm --prefix web run contract:generate`.",
    );
  });
  const expected = new Map(lock.files.map((file) => [file.path, file]));
  const found = new Map(actual.map((file) => [file.path, file]));
  const problems = [
    ...[...expected.keys()].filter((path) => !found.has(path)).map((path) => `missing ${path}`),
    ...[...found.keys()].filter((path) => !expected.has(path)).map((path) => `unexpected ${path}`),
    ...[...found]
      .filter(([path, entry]) => {
        const locked = expected.get(path);
        return (
          locked !== undefined && (locked.sha256 !== entry.sha256 || locked.bytes !== entry.bytes)
        );
      })
      .map(([path]) => `changed ${path}`),
  ];
  if (problems.length > 0) {
    throw new Error(
      `Generated contracts differ from config/contracts.lock.json (${problems.slice(0, 8).join(", ")}${
        problems.length > 8 ? ", ..." : ""
      }). Review the change, then run \`npm --prefix web run contract:lock\`.`,
    );
  }
};
