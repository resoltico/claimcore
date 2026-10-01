import { resolveSourceFile } from "./repository-path.mjs";
import { executable as resolveExecutable } from "./executable.mjs";
// The pinned downloadable tools: config/tools.json names each tool's version and, per platform,
// the asset URL and its SHA-256. Installing verifies the digest before anything is unpacked and
// places the executable in artifacts/tools/bin, which the runners put first on PATH.
import { spawnSync } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import {
  chmodSync,
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  renameSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { setTimeout as sleep } from "node:timers/promises";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * @typedef {object} Asset
 * @property {string} url
 * @property {string} sha256
 * @property {"tar.gz" | "zip" | "file"} archive
 * @property {string} [member] Path of the executable inside the archive.
 */

/** @typedef {{ version: string, assets: Record<string, Asset> }} Tool */

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const attempts = 4;

/**
 * @param {string} root
 * @returns {Record<string, Tool>}
 */
export function loadTools(root) {
  const manifest = JSON.parse(readFileSync(join(root, "config/tools.json"), "utf8"));
  if (manifest.schemaVersion !== 1 || typeof manifest.tools !== "object") {
    throw new Error("config/tools.json must be schema 1 with a tool table.");
  }
  return manifest.tools;
}

/** @returns {string} The platform key used by the manifest, such as `linux-x64`. */
export const platformKey = () => `${process.platform}-${process.arch}`;

/**
 * @param {string} root
 * @returns {string} The directory installed tools are placed in.
 */
export const toolsDirectory = (root) => join(root, "artifacts/tools/bin");

/**
 * @param {string} name
 * @param {Asset} asset
 * @returns {string} The executable's file name on this platform.
 */
function executableName(name, asset) {
  if (asset.archive === "file") {
    return process.platform === "win32" ? `${name}.exe` : name;
  }
  return basename(asset.member ?? name);
}

/** @param {string} url @returns {Promise<Buffer>} */
async function download(url) {
  for (let attempt = 1; ; attempt += 1) {
    try {
      const response = await fetch(url, {
        redirect: "follow",
        signal: AbortSignal.timeout(60_000),
      });
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      return Buffer.from(await response.arrayBuffer());
    } catch (error) {
      if (attempt === attempts) {
        throw new Error(`The pinned asset ${url} is unavailable.`, { cause: error });
      }
      await sleep(2000 * attempt + Math.random() * 1000);
    }
  }
}

/**
 * @param {Buffer} bytes
 * @param {string} name
 * @param {Asset} asset
 * @returns {Buffer} The executable's bytes.
 */
function unpack(bytes, name, asset) {
  if (asset.archive === "file") {
    return bytes;
  }
  const scratch = mkdtempSync(join(tmpdir(), "claimcore-tool-"));
  try {
    const archive = join(scratch, `asset.${asset.archive}`);
    writeFileSync(archive, bytes);
    const member = asset.member ?? name;
    if (member.includes("\\") || member.split("/").some((part) => ["", ".", ".."].includes(part))) {
      throw new Error("A pinned archive member must be a safe relative file path.");
    }
    const extracted = spawnSync(resolveExecutable("tar"), ["-xf", archive, "-C", scratch, member], {
      stdio: "ignore",
    });
    if (extracted.status !== 0) {
      throw new Error(`The ${name} archive does not contain ${member}.`);
    }
    const path = resolveSourceFile(scratch, member);
    return readFileSync(path);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

/** @param {Buffer} bytes @returns {string} */
const digest = (bytes) => createHash("sha256").update(bytes).digest("hex");

/** @param {string} path @param {string} marker @param {Tool} tool @param {Asset} asset */
function installed(path, marker, tool, asset) {
  if (
    !existsSync(path) ||
    !existsSync(marker) ||
    !lstatSync(path).isFile() ||
    lstatSync(path).isSymbolicLink() ||
    !lstatSync(marker).isFile() ||
    lstatSync(marker).isSymbolicLink()
  ) {
    return false;
  }
  try {
    const record = JSON.parse(readFileSync(marker, "utf8"));
    return (
      record.version === tool.version &&
      record.sha256 === asset.sha256 &&
      record.executableSha256 === digest(readFileSync(path))
    );
  } catch {
    return false;
  }
}

/**
 * Install one tool for this platform unless the verified version is already in place.
 * @param {string} root
 * @param {string} name
 * @param {{ tools?: Record<string, Tool>, platform?: string, acquire?: (url: string) => Promise<Buffer> }} [options]
 * @returns {Promise<string>} The executable's path.
 */
export async function installTool(
  root,
  name,
  { tools = loadTools(root), platform = platformKey(), acquire = download } = {},
) {
  if (!/^[a-z][a-z0-9-]*$/u.test(name)) {
    throw new Error("A pinned tool requires a safe executable name.");
  }
  const tool = tools[name];
  const asset = tool?.assets[platform];
  if (!tool || !asset) {
    throw new Error(`Tool '${name}' has no pinned asset for ${platform}.`);
  }
  const directory = toolsDirectory(root);
  const executable = join(directory, executableName(name, asset));
  const marker = join(directory, `.${name}.json`);
  if (installed(executable, marker, tool, asset)) {
    return executable;
  }
  const bytes = await acquire(asset.url);
  if (digest(bytes) !== asset.sha256.toLowerCase()) {
    throw new Error(`The ${name} asset failed integrity verification.`);
  }
  mkdirSync(directory, { recursive: true });
  const unpacked = unpack(bytes, name, asset);
  const record = JSON.stringify({
    version: tool.version,
    sha256: asset.sha256,
    executableSha256: digest(unpacked),
  });
  const scratch = `${executable}.${randomUUID()}.partial`;
  const pendingMarker = `${scratch}.json`;
  try {
    writeFileSync(scratch, unpacked);
    chmodSync(scratch, 0o755);
    writeFileSync(pendingMarker, record);
    renameSync(scratch, executable);
    renameSync(pendingMarker, marker);
  } finally {
    rmSync(scratch, { force: true });
    rmSync(pendingMarker, { force: true });
  }
  return executable;
}

/**
 * PATH with the installed tools first.
 * @param {string} root
 * @param {string} [current]
 * @returns {string}
 */
export const pathWithTools = (root, current = process.env["PATH"] ?? "") =>
  `${toolsDirectory(root)}${process.platform === "win32" ? ";" : ":"}${current}`;

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const requested = process.argv.slice(2);
  const tools = loadTools(repositoryRoot);
  const names = requested.length > 0 ? requested : Object.keys(tools);
  for (const name of names) {
    process.stdout.write(`${await installTool(repositoryRoot, name, { tools })}\n`);
  }
  const githubPath = process.env["GITHUB_PATH"];
  if (githubPath !== undefined) {
    writeFileSync(githubPath, `${toolsDirectory(repositoryRoot)}\n`, { flag: "a" });
  }
}
