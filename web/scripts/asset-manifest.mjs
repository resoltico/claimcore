import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readdir, readFile, rename, writeFile } from "node:fs/promises";
import { basename, dirname, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const webDirectory = resolve(scriptDirectory, "..");
const distDirectory = resolve(webDirectory, "dist");
const manifestFileName = "claimcore-assets.manifest.json";
const manifestPath = resolve(distDirectory, manifestFileName);

const asRelativePath = (path) => relative(webDirectory, path).split(sep).join("/");
const hash = (contents) => createHash("sha256").update(contents).digest("hex");
const readHash = async (path) => hash(await readFile(path));

const filesBelow = async (directory) => {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = await Promise.all(
    entries
      .sort((left, right) => left.name.localeCompare(right.name))
      .map(async (entry) => {
        const path = resolve(directory, entry.name);
        if (entry.isDirectory()) return filesBelow(path);
        if (entry.isFile()) return [path];
        throw new Error(`Asset inputs cannot contain a symbolic link or special file: ${path}.`);
      }),
  );
  return files.flat();
};

const sourceFiles = async () => {
  const source = await filesBelow(resolve(webDirectory, "src"));
  const scripts = await filesBelow(resolve(webDirectory, "scripts"));
  const fixed = [
    ".npmrc",
    "index.html",
    "package.json",
    "tsconfig.app.json",
    "tsconfig.json",
    "tsconfig.node.json",
    "vite.config.ts",
  ].map((file) => resolve(webDirectory, file));
  return [...source, ...scripts, ...fixed]
    .filter((path) => !asRelativePath(path).startsWith("src/generated/"))
    .sort((left, right) => asRelativePath(left).localeCompare(asRelativePath(right)));
};

const recordsFor = async (root, excluded) => {
  const files = await filesBelow(root);
  const records = await Promise.all(
    files
      .filter((path) => basename(path) !== excluded)
      .map(async (path) => ({
        path: relative(root, path).split(sep).join("/"),
        sha256: await readHash(path),
        bytes: (await readFile(path)).byteLength,
      })),
  );
  return records.sort((left, right) => left.path.localeCompare(right.path));
};

const treeHash = (records) =>
  hash(records.map((record) => `${record.path}\n${record.sha256}\n`).join(""));

const requiredRuntime = async () => {
  const packageManifest = JSON.parse(await readFile(resolve(webDirectory, "package.json"), "utf8"));
  const node = `v${packageManifest.engines?.node ?? ""}`;
  const npm = packageManifest.engines?.npm;
  const actualNpm = execFileSync("npm", ["--version"], { encoding: "utf8" }).trim();
  if (node !== process.version || npm !== actualNpm)
    throw new Error(
      "ClaimCore Web assets require the exact Node.js and npm versions in package.json.",
    );
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
    formatVersion: 1,
    runtime,
    inputs: { webSourceSha256: source, packageLockSha256, generatedContractSha256: contractSha256 },
    assets,
  };
};

const canonicalManifest = (manifest) => `${JSON.stringify(manifest, null, 2)}\n`;

export const writeManifest = async () => {
  const temporary = `${manifestPath}.${process.pid}.tmp`;
  await writeFile(temporary, canonicalManifest(await createManifest()), { mode: 0o600 });
  await rename(temporary, manifestPath);
};

export const verifyManifest = async () => {
  const expected = canonicalManifest(await createManifest());
  const actual = await readFile(manifestPath, "utf8");
  if (actual !== expected)
    throw new Error(
      "ClaimCore Web asset manifest does not bind this exact dist tree and build inputs.",
    );
};
