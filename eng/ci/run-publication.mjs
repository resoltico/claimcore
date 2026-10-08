// A standalone publication is a requested deliverable, not disposable qualification scratch.
import assert from "node:assert/strict";
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { runContext } from "./run-context.mjs";
import { artifactDirectory } from "./artifact-path.mjs";
import { regularFiles, fingerprint } from "./scan/files.mjs";
import { scanArtifacts } from "./scan/artifacts.mjs";
import { installTool } from "./tools.mjs";
import { products, verifyPublished } from "./publish/main.mjs";
import { loadSuites } from "./suites/registry.mjs";
import { buildSuites } from "./suites/build.mjs";

/** One native solution shares application dependencies while preserving harness prerequisites.
 * @param {string} root @returns {import("./suites/registry.mjs").Suite} */
export function publicationBuildSuite(root) {
  const suite = loadSuites(root).find((item) => item.id === "acceptance");
  assert.ok(suite?.kind === "dotnet", "The registered acceptance harness is required.");
  assert.equal(suite.configuration ?? "Release", "Release");
  return { ...suite, build: [...products.map((item) => item.project), ...(suite.build ?? [])] };
}

/** @param {string} root */
export function buildPublicationInputs(root) {
  buildSuites(root, [publicationBuildSuite(root)]);
}

/** @param {import("./run-context.mjs").RunContext} context @param {string} output */
export async function retainPublication(context, output) {
  const input = artifactDirectory(context.source, output);
  assert.deepEqual(readdirSync(input).sort(), ["cli", "database", "manifests", "web"]);
  assert.deepEqual(readdirSync(join(input, "manifests")).sort(), [
    "cli.json",
    "database.json",
    "web.json",
  ]);
  verifyPublished(input);
  const before = fingerprint(input);
  assert.equal(
    await scanArtifacts([input], { gitleaks: () => installTool(context.source, "gitleaks") }),
    0,
  );
  const destination = artifactDirectory(context.origin, `artifacts/runs/${context.id}/publication`);
  assert.ok(!existsSync(destination));
  mkdirSync(destination, { mode: 0o700 });
  for (const file of regularFiles(input)) {
    const target = join(destination, relative(input, file));
    mkdirSync(dirname(target), { recursive: true, mode: 0o700 });
    copyFileSync(file, target);
    assert.deepEqual(readFileSync(target), readFileSync(file));
  }
  assert.equal(fingerprint(input), before);
  verifyPublished(destination);
  console.log(`Retained verified publication: ${destination}.`);
}

/** Called by a wrapper after its actual consumers and exact-label cleanup have settled.
 * @param {import("./run-context.mjs").RunContext} context @param {string} workspace
 */
export async function disposeQualifiedPublication(context, workspace) {
  workspace = artifactDirectory(context.source, workspace);
  assert.match(
    relative(context.source, workspace).replaceAll("\\", "/"),
    /^artifacts\/(?:acceptance-local|local-browser)\.[A-Za-z0-9]+$/u,
  );
  const publication = join(workspace, "publish");
  verifyPublished(publication);
  const manifests = join(publication, "manifests");
  assert.deepEqual(readdirSync(manifests).sort(), ["cli.json", "database.json", "web.json"]);
  const retained = join(workspace, "manifests");
  assert.ok(!existsSync(retained));
  assert.equal(
    await scanArtifacts([manifests], { gitleaks: () => installTool(context.source, "gitleaks") }),
    0,
  );
  mkdirSync(retained, { mode: 0o700 });
  for (const file of regularFiles(manifests)) {
    copyFileSync(file, join(retained, relative(manifests, file)));
  }
  regularFiles(publication);
  rmSync(publication, { recursive: true });
}
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const root = resolve(import.meta.dirname, "../..");
  const context = runContext(root);
  const [mode, workspace] = process.argv.slice(2);
  assert.ok(context, "Publication work requires its admitted run context.");
  if (mode === "build-inputs" && process.argv.length === 3) {
    buildPublicationInputs(root);
  } else {
    assert.ok(mode === "dispose" && workspace && process.argv.length === 4);
    await disposeQualifiedPublication(context, workspace);
  }
}
