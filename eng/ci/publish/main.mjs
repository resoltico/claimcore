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
import { renderNotices } from "../policy/notices.mjs";
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
 * @returns {string}
 */
function projectVersion(project) {
  const version = run("dotnet", [
    "msbuild",
    project,
    "-nologo",
    "-verbosity:quiet",
    "-property:Configuration=Release",
    "-getProperty:Version",
  ]).trim();
  if (version === "") {
    throw new Error(`${project} has no version.`);
  }
  return version;
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
  const version = projectVersion(project);
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
  const sbom = JSON.parse(readFileSync(join(destination, `${product}.cdx.json`), "utf8"));
  writeFileSync(join(destination, "THIRD-PARTY-NOTICES.txt"), renderNotices(sbom));
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
