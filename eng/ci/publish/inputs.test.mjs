import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import {
  cpSync,
  copyFileSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  statSync,
  utimesSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { join, resolve } from "node:path";
import test from "node:test";
import { copySource } from "../source-snapshot.mjs";
import { compiledInputs, producingInputDigest, verifyProducingInputs } from "./inputs.mjs";
import { verifyPublished } from "./main.mjs";
import { verifyTree, writeManifest } from "./tree.mjs";
import { verifyLabelChange } from "./label-change-probe.mjs";
import { verifyStaleRestore } from "./restore-graph-probe.mjs";

const root = resolve(import.meta.dirname, "../../..");

/** @param {string} directory @param {string[]} args */
function dotnet(directory, args) {
  const result = spawnSync("dotnet", args, { cwd: directory, encoding: "utf8", timeout: 600_000 });
  assert.equal(result.status, 0, `Producing-input fixture command failed: ${args[0]}.`);
}

/** @typedef {{scratch: string, source: string, published: string}} Fixture */
/** @param {(fixture: Fixture) => void} action */
function withPublication(action) {
  const scratch = mkdtempSync(join(root, "artifacts/publication-inputs-"));
  const source = join(scratch, "source");
  const published = join(scratch, "published");
  try {
    copySource(root, source);
    dotnet(source, ["restore", "src/ClaimCore.Cli/ClaimCore.Cli.fsproj", "--locked-mode"]);
    dotnet(source, ["tool", "restore"]);
    publishCli(source, published);
    assert.deepEqual(verifyPublished(published, ["cli"]), ["cli"]);
    action({ scratch, source, published });
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

/** @param {string} source @param {string} destination */
function publishCli(source, destination) {
  dotnet(source, [
    "publish",
    "src/ClaimCore.Cli/ClaimCore.Cli.fsproj",
    "--configuration",
    "Release",
    "--no-restore",
    "-p:UseAppHost=false",
    "--output",
    join(destination, "cli"),
  ]);
  mkdirSync(join(destination, "manifests"));
  const inputs = compiledInputs(join(destination, "cli"));
  writeManifest(
    "ClaimCore.Cli",
    join(destination, "cli"),
    join(destination, "manifests/cli.json"),
    inputs,
  );
  return inputs;
}

/** @param {Fixture} fixture */
function changeInputs({ source, scratch }) {
  const before = producingInputDigest(source);
  const policy = join(source, "src/ClaimCore.Domain/FieldDefinitions.fs");
  writeFileSync(
    policy,
    readFileSync(policy, "utf8").replace(
      '"Handler\'s case reference"',
      '"Synthetic reference label probe"',
    ),
  );
  const native = join(source, "src/ClaimCore.HostSecurity/native/claimcore_private_openat.c");
  const timestamp = statSync(native);
  writeFileSync(
    native,
    `${readFileSync(native, "utf8")}\nint claimcore_publication_source_probe(void) { return 37; }\n`,
  );
  utimesSync(native, timestamp.atime, timestamp.mtime);
  const changed = producingInputDigest(source);
  assert.notEqual(changed, before);
  const stale = spawnSync(
    "node",
    ["eng/ci/publish/main.mjs", "build", "--no-build", "--output", join(scratch, "stale")],
    { cwd: source, encoding: "utf8", timeout: 600_000 },
  );
  assert.equal(stale.status, 1);
  assert.match(stale.stderr, /produced from different inputs/u);
  return changed;
}

/** @param {string} source @param {string} scratch @param {Record<string, string>} actual @param {string} changed */
function verifyCommitLabels(source, scratch, actual, changed) {
  execFileSync("git", ["init", "--quiet"], { cwd: source });
  const commit = (/** @type {string} */ message) =>
    execFileSync(
      "git",
      [
        "-c",
        "user.name=Synthetic fixture",
        "-c",
        "user.email=fixture@example.invalid",
        "-c",
        "commit.gpgsign=false",
        "-c",
        `core.hooksPath=${join(scratch, "no-hooks")}`,
        "commit",
        "--allow-empty",
        "--quiet",
        "-m",
        message,
      ],
      { cwd: source },
    );
  commit("First producing label");
  const first = execFileSync("git", ["rev-parse", "HEAD"], { cwd: source, encoding: "utf8" });
  commit("Equivalent producing inputs under another label");
  const second = execFileSync("git", ["rev-parse", "HEAD"], { cwd: source, encoding: "utf8" });
  assert.notEqual(first, second);
  assert.equal(producingInputDigest(source), changed);
  verifyProducingInputs(actual, producingInputDigest(source));
}

/** @param {Fixture} fixture @param {string} changed */
function verifyDifferentPublication({ source, scratch, published }, changed) {
  const differing = join(scratch, "differing");
  const actual = publishCli(source, differing);
  verifyProducingInputs(actual, changed);
  assert.match(
    verifyTree("ClaimCore.Cli", join(differing, "cli"), join(differing, "manifests/cli.json")),
    /^[0-9a-f]{64}$/u,
  );
  assert.throws(() => verifyPublished(differing, ["cli"]), /produced from different inputs/u);
  const mixed = join(scratch, "mixed");
  cpSync(differing, mixed, { recursive: true });
  copyFileSync(
    join(published, "cli/ClaimCore.Domain.dll"),
    join(mixed, "cli/ClaimCore.Domain.dll"),
  );
  rmSync(join(mixed, "manifests/cli.json"));
  const inputs = compiledInputs(join(mixed, "cli"));
  writeManifest("ClaimCore.Cli", join(mixed, "cli"), join(mixed, "manifests/cli.json"), inputs);
  assert.match(
    verifyTree("ClaimCore.Cli", join(mixed, "cli"), join(mixed, "manifests/cli.json")),
    /^[0-9a-f]{64}$/u,
  );
  assert.throws(() => verifyProducingInputs(inputs, changed), /different inputs/u);
  verifyCommitLabels(source, scratch, actual, changed);
}

/** @param {Fixture} fixture */
function verifyMixedProducts({ source, scratch, published }) {
  const mixed = join(scratch, "mixed-products");
  cpSync(published, mixed, { recursive: true });
  const project = "src/ClaimCore.Database/ClaimCore.Database.fsproj";
  dotnet(source, ["restore", project, "--locked-mode"]);
  dotnet(source, [
    "publish",
    project,
    "--configuration",
    "Release",
    "--no-restore",
    "-p:UseAppHost=false",
    "--output",
    join(mixed, "database"),
  ]);
  writeManifest(
    "ClaimCore.Database",
    join(mixed, "database"),
    join(mixed, "manifests/database.json"),
    compiledInputs(join(mixed, "database")),
  );
  assert.match(
    verifyTree(
      "ClaimCore.Database",
      join(mixed, "database"),
      join(mixed, "manifests/database.json"),
    ),
    /^[0-9a-f]{64}$/u,
  );
  assert.throws(() => verifyPublished(mixed, ["cli", "database"]), /different inputs/u);
}

/** @param {Fixture} fixture */
function verifyNativeNoBuild({ source, scratch, published }) {
  const native = readdirSync(join(published, "cli")).find((path) =>
    path.startsWith("libclaimcore_hostsecurity_native."),
  );
  if (process.platform === "win32") {
    assert.equal(native, undefined, "Unsupported private runtime has no native shim.");
    return;
  }
  assert.ok(native, "Supported private runtime publishes its native shim.");
  const retained = join(source, "artifacts/native/ClaimCore.Cli/Release", native);
  copyFileSync(join(published, "cli", native), retained);
  const future = new Date(Date.now() + 60_000);
  utimesSync(retained, future, future);
  const output = join(scratch, "native-no-build");
  dotnet(source, [
    "publish",
    "src/ClaimCore.Cli/ClaimCore.Cli.fsproj",
    "--configuration",
    "Release",
    "--no-restore",
    "--no-build",
    "-p:UseAppHost=false",
    "--output",
    output,
  ]);
  const symbols = execFileSync("nm", [join(output, native)], { encoding: "utf8" });
  assert.ok(
    symbols.includes("claimcore_publication_source_probe"),
    "Publication rebuilt the native source despite retained newer output timestamps.",
  );
  verifyProducingInputs(compiledInputs(output), producingInputDigest(source));
}

test(
  "real compiled identities reject differing-input publications, stale no-build and mixed provenance",
  { timeout: 900_000 },
  () => {
    withPublication((fixture) => {
      verifyStaleRestore(fixture.source, fixture.scratch);
      const changed = changeInputs(fixture);
      verifyDifferentPublication(fixture, changed);
      verifyMixedProducts(fixture);
      verifyNativeNoBuild(fixture);
      verifyLabelChange(fixture.source, join(fixture.scratch, "label-publication"));
    });
  },
);
