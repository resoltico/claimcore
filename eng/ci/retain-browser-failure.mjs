import {
  closeSync,
  constants,
  existsSync,
  fstatSync,
  ftruncateSync,
  lstatSync,
  mkdirSync,
  openSync,
  readFileSync,
  readdirSync,
  readSync,
  writeFileSync,
} from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { assertNoLinkAbove } from "./scan/files.mjs";
import { protectScratch } from "./scratch-privacy.mjs";

const diagnosticLimit = 16 * 1024 * 1024;
const retainedNames = ["web-host.log", "playwright.log", "cli-acceptance.log"];

/** @param {string} path */
function regularDiagnostic(path) {
  assertNoLinkAbove(path);
  const stat = lstatSync(path);
  if (!stat.isFile() || stat.nlink !== 1 || !Number.isSafeInteger(stat.size)) {
    throw new Error("Completed diagnostics require physical, unlinked regular files.");
  }
  return stat;
}

/** @param {number} descriptor @param {number} length @param {number} start */
function readTail(descriptor, length, start) {
  const bytes = Buffer.alloc(length);
  let offset = 0;
  while (offset < bytes.length) {
    const count = readSync(descriptor, bytes, offset, bytes.length - offset, start + offset);
    if (count === 0) {
      throw new Error("Completed CLI diagnostic changed during bounded capture.");
    }
    offset += count;
  }
  return bytes;
}

/** Called after the native acceptance command completes, before the sensitive-output scan.
 * @param {string} directory @returns {boolean} Whether overflow refused complete diagnostics. */
export function boundCompletedCliLog(directory) {
  const path = join(resolve(directory), "cli-acceptance.log");
  const before = regularDiagnostic(path);
  if (before.size <= diagnosticLimit) {
    return false;
  }
  const descriptor = openSync(path, constants.O_RDWR | (constants.O_NOFOLLOW ?? 0));
  try {
    const opened = fstatSync(descriptor);
    if (
      opened.dev !== before.dev ||
      opened.ino !== before.ino ||
      opened.size !== before.size ||
      opened.nlink !== 1
    ) {
      throw new Error("Completed CLI diagnostic identity changed.");
    }
    const marker = Buffer.from(
      `[CLI diagnostic exceeded ${diagnosticLimit} bytes; originalBytes=${before.size}; preceding output omitted; tail retained.]\n`,
    );
    const length = diagnosticLimit - marker.length;
    const tail = readTail(descriptor, length, before.size - length);
    if (fstatSync(descriptor).size !== before.size) {
      throw new Error("Completed CLI diagnostic changed during bounded capture.");
    }
    ftruncateSync(descriptor, 0);
    writeFileSync(descriptor, Buffer.concat([marker, tail]));
    return true;
  } finally {
    closeSync(descriptor);
  }
}

/** Called only after the completed fixture's sensitive-output scan succeeds.
 * @param {string} source @param {string} destination */
export function retainBrowserFailure(source, destination) {
  source = resolve(source);
  destination = resolve(destination);
  assertNoLinkAbove(source);
  if (!lstatSync(source).isDirectory()) {
    throw new Error("Completed diagnostic source must be a physical directory.");
  }
  const available = readdirSync(source);
  const files = retainedNames
    .filter((name) => available.includes(name))
    .map((name) => {
      const path = join(source, name);
      if (regularDiagnostic(path).size > diagnosticLimit) {
        throw new Error("Completed diagnostic exceeds its bounded size.");
      }
      return { name, bytes: readFileSync(path) };
    });
  let ancestor = dirname(destination);
  while (!existsSync(ancestor)) {
    ancestor = dirname(ancestor);
  }
  assertNoLinkAbove(ancestor);
  mkdirSync(dirname(destination), { mode: 0o700, recursive: true });
  assertNoLinkAbove(dirname(destination));
  mkdirSync(destination, { mode: 0o700 });
  protectScratch(destination);
  for (const { name, bytes } of files) {
    writeFileSync(join(destination, name), bytes, { flag: "wx", mode: 0o600 });
    regularDiagnostic(join(source, name));
    if (!readFileSync(join(source, name)).equals(bytes)) {
      throw new Error("Completed diagnostic changed during retention.");
    }
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    const [source, destination] = process.argv.slice(2);
    if (source === "bound-cli" && destination !== undefined && process.argv.length === 4) {
      process.exitCode = boundCompletedCliLog(destination) ? 1 : 0;
    } else if (source !== undefined && destination !== undefined && process.argv.length === 4) {
      retainBrowserFailure(source, destination);
      console.log(`Private browser/CLI failure diagnostics retained at ${resolve(destination)}.`);
    } else {
      throw new Error("Diagnostic command arguments were refused.");
    }
  } catch {
    console.error("Private browser/CLI failure diagnostics were refused.");
    process.exitCode = 1;
  }
}
