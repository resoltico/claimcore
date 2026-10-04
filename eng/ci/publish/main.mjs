import { commandLine } from "../executable.mjs";
// Publish the three applications once, describe them (SBOM, third-party notices, manifest) and let
// every consumer verify the exact bytes it received.
//
//   node eng/ci/publish/main.mjs build [--output DIR] [--no-build]
//   node eng/ci/publish/main.mjs verify DIR [product...]
import { spawnSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, isAbsolute, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { flag, option } from "../process-support.mjs";
import { packageNoticeReader } from "../policy/nuget-notices.mjs";
import { productComponentNames, renderNotices } from "../policy/notices.mjs";
import { verifyTree, writeManifest } from "./tree.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");

/** @type {{ product: string, directory: string, project: string, frontendSbom?: string }[]} */
export const products = [
  { product: "ClaimCore.Cli", directory: "cli", project: "src/ClaimCore.Cli/ClaimCore.Cli.fsproj" },
  {
    product: "ClaimCore.Database",
    directory: "database",
    project: "src/ClaimCore.Database/ClaimCore.Database.fsproj",
  },
  {
    product: "ClaimCore.Web",
    directory: "web",
    project: "src/ClaimCore.Web/ClaimCore.Web.fsproj",
    frontendSbom: "artifacts/sbom/claimcore-web.cdx.json",
  },
];

/**
 * @param {string} command
 * @param {string[]} args
 * @returns {string} Standard output.
 */
function run(command, args) {
  const result = spawnSync(...commandLine(command, args), {
    cwd: root,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "inherit"],
  });
  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(" ")} failed.`);
  }
  return result.stdout;
}

/**
 * @param {string} project
 * @returns {{ version: string, license: string, assetsPath: string }}
 */
function projectIdentity(project) {
  const values = run("dotnet", [
    "msbuild",
    project,
    "-nologo",
    "-verbosity:quiet",
    "-property:Configuration=Release",
    "-getProperty:Version,PackageLicenseExpression,ProjectAssetsFile",
  ]);
  const {
    Version: version,
    PackageLicenseExpression: license,
    ProjectAssetsFile: assetsPath,
  } = JSON.parse(values).Properties;
  if (
    typeof version !== "string" ||
    version.trim() === "" ||
    typeof license !== "string" ||
    license.trim() === "" ||
    typeof assetsPath !== "string" ||
    assetsPath.trim() === ""
  ) {
    throw new Error(`${project} lacks version or license metadata.`);
  }
  return { version, license, assetsPath };
}

/**
 * @typedef {{ name?: string, licenses?: unknown }} ProjectComponent
 */
/**
 * @param {{ metadata?: { component?: ProjectComponent }, components?: ProjectComponent[] }} sbom
 * @param {string} license The evaluated SPDX expression for the project.
 */
export function licenseProjectComponents(sbom, license) {
  const product = sbom.metadata?.component;
  if (
    product === undefined ||
    !productComponentNames.has(product.name ?? "") ||
    license.trim() === ""
  ) {
    throw new Error("The SBOM lacks a registered product or project license.");
  }
  for (const component of [product, ...(sbom.components ?? [])]) {
    if (productComponentNames.has(component.name ?? "")) {
      component.licenses = [{ expression: license }];
    }
  }
  return sbom;
}

/**
 * @param {{ product: string, project: string }} item
 * @param {string} destination
 * @param {{ version: string, license: string, assetsPath: string }} identity
 */
function describePublishedDependencies(
  { product, project },
  destination,
  { version, license, assetsPath },
) {
  run("dotnet", [
    "tool",
    "run",
    "dotnet-CycloneDX",
    "--",
    project,
    "--exclude-dev",
    "--disable-package-restore",
    "--configuration",
    "Release",
    "--output",
    destination,
    "--filename",
    `${product}.cdx.json`,
    "--output-format",
    "Json",
    "--no-serial-number",
    "--set-name",
    product,
    "--set-version",
    version,
    "--include-project-references",
  ]);
  const sbomPath = join(destination, `${product}.cdx.json`);
  const sbom = licenseProjectComponents(JSON.parse(readFileSync(sbomPath, "utf8")), license);
  writeFileSync(sbomPath, `${JSON.stringify(sbom, null, 2)}\n`);
  writeFileSync(
    join(destination, "THIRD-PARTY-NOTICES.txt"),
    renderNotices(sbom, packageNoticeReader(assetsPath)),
  );
}

/**
 * @param {(typeof products)[number]} item
 * @param {{ output: string, build: boolean }} options
 */
function publishOne({ product, directory, project, frontendSbom }, { output, build }) {
  const destination = join(output, directory);
  if (existsSync(destination)) {
    throw new Error(`The output ${destination} must start absent.`);
  }
  const { version, license, assetsPath } = projectIdentity(project);
  run("dotnet", [
    "publish",
    project,
    "--configuration",
    "Release",
    ...(build ? [] : ["--no-build"]),
    "--no-restore",
    "--output",
    destination,
    "-p:UseAppHost=false",
  ]);
  describePublishedDependencies({ product, project }, destination, {
    version,
    license,
    assetsPath,
  });
  if (frontendSbom !== undefined) {
    copyFileSync(join(root, frontendSbom), join(destination, `${product}.frontend.cdx.json`));
  }
  mkdirSync(join(output, "manifests"), { recursive: true });
  writeManifest(product, destination, join(output, "manifests", `${directory}.json`));
}

/**
 * @param {string} output
 * @param {string[]} [only] Directory names; every product when empty.
 * @returns {string[]} The verified directories.
 */
export function verifyPublished(output, only = []) {
  const chosen = products.filter((item) => only.length === 0 || only.includes(item.directory));
  if (chosen.length === 0 || chosen.length !== (only.length || products.length)) {
    throw new Error("Name published products: cli, database, web.");
  }
  for (const { product, directory } of chosen) {
    verifyTree(product, join(output, directory), join(output, "manifests", `${directory}.json`));
  }
  return chosen.map((item) => item.directory);
}

/** @param {string} path */
const absolute = (path) => (isAbsolute(path) ? path : resolve(root, path));

/** @param {string[]} argv */
function main(argv) {
  const [mode, ...rest] = argv;
  if (mode === "build") {
    const output = absolute(option(rest, "output", "artifacts/publish"));
    for (const item of products) {
      publishOne(item, { output, build: !flag(rest, "no-build") });
    }
    process.stdout.write(`Published and verified ${verifyPublished(output).join(", ")}.\n`);
  } else if (mode === "verify" && rest[0] !== undefined) {
    const [output = "", ...only] = rest;
    process.stdout.write(`Verified ${verifyPublished(absolute(output), only).join(", ")}.\n`);
  } else {
    throw new Error("usage: main.mjs build [--output DIR] [--no-build] | verify DIR [product...]");
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    main(process.argv.slice(2));
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  }
}
