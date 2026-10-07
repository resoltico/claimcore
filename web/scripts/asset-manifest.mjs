import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readdir, readFile, rename, writeFile } from "node:fs/promises";
import { basename, dirname, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { compilerInputs } from "./compiler-inputs.mjs";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const webDirectory = resolve(scriptDirectory, "..");
const distDirectory = resolve(webDirectory, "dist");
const manifestFileName = "claimcore-assets.manifest.json";
const manifestPath = resolve(distDirectory, manifestFileName);

/** @param {string} path */
const asRelativePath = (path) => relative(webDirectory, path).split(sep).join("/");
/** @param {string | NodeJS.ArrayBufferView} contents */
const hash = (contents) => createHash("sha256").update(contents).digest("hex");
/** @param {string} path */
const readHash = async (path) => hash(await readFile(path));

/** @param {string} left @param {string} right */
const compareOrdinal = (left, right) => {
  if (left === right) {
    return 0;
  }
  return left < right ? -1 : 1;
};

/** @param {string} directory @returns {Promise<string[]>} */
const filesBelow = async (directory) => {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = await Promise.all(
    entries
      .sort((left, right) => compareOrdinal(left.name, right.name))
      .map((entry) => {
        const path = resolve(directory, entry.name);
        if (entry.isDirectory()) {
          return filesBelow(path);
        }
        if (entry.isFile()) {
          return [path];
        }
        throw new Error(`Asset inputs cannot contain a symbolic link or special file: ${path}.`);
      }),
  );
  return files.flat();
};

export const sourceFiles = async () => {
  const source = await filesBelow(resolve(webDirectory, "src"));
  const scripts = await filesBelow(resolve(webDirectory, "scripts"));
  const fixed = [
    ".npmrc",
    "index.html",
    "../LICENSE",
    "package.json",
    "tsconfig.app.json",
    "tsconfig.json",
    "tsconfig.node.json",
    "vite.config.ts",
    "../eng/ci/repository-path.mjs",
    "../eng/lint/jsonc.mjs",
  ].map((file) => resolve(webDirectory, file));
  const compiler = compilerInputs(
    resolve(webDirectory, ".."),
    fixed.filter((file) => basename(file).startsWith("tsconfig")),
  );
  return [...new Set([...source, ...scripts, ...fixed, ...compiler])]
    .filter((path) => !asRelativePath(path).startsWith("src/generated/"))
    .sort((left, right) => compareOrdinal(asRelativePath(left), asRelativePath(right)));
};

/** @param {string} root @param {string} [excluded] */
export const recordsFor = async (root, excluded) => {
  const files = await filesBelow(root);
  const records = await Promise.all(
    files
      .filter((path) => relative(root, path).split(sep).join("/") !== excluded)
      .map(async (path) => ({
        path: relative(root, path).split(sep).join("/"),
        sha256: await readHash(path),
        bytes: (await readFile(path)).byteLength,
      })),
  );
  return records.sort((left, right) => compareOrdinal(left.path, right.path));
};

/** @param {{ path: string, sha256: string }[]} records */
export const treeHash = (records) =>
  hash(JSON.stringify(records.map(({ path, sha256 }) => [path, sha256])));

const requiredRuntime = async () => {
  const packageManifest = JSON.parse(await readFile(resolve(webDirectory, "package.json"), "utf8"));
  const node = `v${packageManifest.engines?.node ?? ""}`;
  const npm = packageManifest.engines?.npm;
  const actualNpm = execFileSync("npm", ["--version"], { encoding: "utf8" }).trim();
  if (node !== process.version || npm !== actualNpm) {
    throw new Error(
      "ClaimCore Web assets require the exact Node.js and npm versions in package.json.",
    );
  }
  return { node, npm };
};

const generatedContractHash = async () =>
  treeHash(await recordsFor(resolve(webDirectory, "src/generated")));

const createManifest = async () => {
  const [runtime, source, assets, packageLockSha256, contractSha256] = await Promise.all([
    requiredRuntime(),
    sourceFiles().then(async (files) =>
      treeHash(
        await Promise.all(
          files.map(async (path) => ({ path: asRelativePath(path), sha256: await readHash(path) })),
        ),
      ),
    ),
    recordsFor(distDirectory, manifestFileName),
    readHash(resolve(webDirectory, "package-lock.json")),
    generatedContractHash(),
  ]);
  return {
    format: "claimcore-web-assets",
    formatVersion: 2,
    runtime,
    inputs: { webSourceSha256: source, packageLockSha256, generatedContractSha256: contractSha256 },
    assets,
  };
};

/** @param {Awaited<ReturnType<typeof createManifest>>} manifest */
const canonicalManifest = (manifest) => `${JSON.stringify(manifest, null, 2)}\n`;

export const writeManifest = async () => {
  const temporary = `${manifestPath}.${process.pid}.tmp`;
  await writeFile(temporary, canonicalManifest(await createManifest()), { mode: 0o600 });
  await rename(temporary, manifestPath);
};

export const verifyManifest = async () => {
  const expected = canonicalManifest(await createManifest());
  const actual = await readFile(manifestPath, "utf8");
  if (actual !== expected) {
    throw new Error(
      "ClaimCore Web asset manifest does not bind this exact dist tree and build inputs.",
    );
  }
};
