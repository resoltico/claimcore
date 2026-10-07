import { executable } from "../executable.mjs";
// Real SDK roots/configuration pivots must isolate native compilation and publication.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { compiledInputs, producingInputDigest, verifyProducingInputs } from "./inputs.mjs";

/** @param {string} source @param {string[]} args */
function dotnet(source, args) {
  const result = spawnSync(executable("dotnet"), args, {
    cwd: source,
    encoding: "utf8",
    timeout: 600_000,
  });
  assert.equal(result.status, 0, "Real SDK native output-root qualification failed.");
  return result.stdout;
}
/** @param {string} source @param {string} project @param {string} artifacts @param {string} configuration @param {string} name */
function verifyRidNative(source, project, artifacts, configuration, name) {
  const properties = [`-p:ArtifactsPath=${artifacts}`, `-p:Configuration=${configuration}`];
  /** @type {string} */
  const rid = `${process.platform === "darwin" ? "osx" : "linux"}-${process.arch}`;
  const nativeFile = dotnet(source, [
    "msbuild",
    project,
    "-target:BuildHostSecurityNative",
    "-getProperty:HostSecurityNativeFile",
    ...properties,
    `-p:RuntimeIdentifier=${rid}`,
  ]).trim();
  const pivoted = join(
    artifacts,
    "obj/ClaimCore.Cli",
    `${configuration.toLowerCase()}_${rid}`,
    "native",
    name,
  );
  assert.equal(nativeFile, pivoted);
  assert.ok(statSync(pivoted).isFile());
}
/** @param {string} source @param {string} project @param {string} publication @param {string[]} properties */
function publishPivot(source, project, publication, properties) {
  dotnet(source, ["restore", project, "--locked-mode", ...properties]);
  dotnet(source, [
    "publish",
    project,
    "--no-restore",
    "-p:UseAppHost=false",
    "--output",
    publication,
    ...properties,
  ]);
}
/** @param {string} source @param {string} scratch */
export function verifyNativeOutputRoots(source, scratch) {
  if (process.platform === "win32") {
    return;
  }
  const project = "src/ClaimCore.Cli/ClaimCore.Cli.fsproj";
  const name = `libclaimcore_hostsecurity_native.${process.platform === "darwin" ? "dylib" : "so"}`;
  let retained;
  for (const [id, configuration] of [
    ["first", "Release"],
    ["second", "Debug"],
  ]) {
    assert.ok(id && configuration);
    const artifacts = join(source, "artifacts", `native-root-${id}`);
    const properties = [`-p:ArtifactsPath=${artifacts}`, `-p:Configuration=${configuration}`];
    const publication = join(scratch, `native-${id}`);
    publishPivot(source, project, publication, properties);
    assert.deepEqual(
      readdirSync(publication).filter((file) =>
        file.startsWith("libclaimcore_hostsecurity_native."),
      ),
      [name],
    );
    verifyProducingInputs(compiledInputs(publication), producingInputDigest(source));
    const intermediate = join(
      artifacts,
      "obj/ClaimCore.Cli",
      configuration.toLowerCase(),
      "native",
      name,
    );
    assert.deepEqual(readFileSync(intermediate), readFileSync(join(publication, name)));
    if (retained !== undefined) {
      assert.equal(statSync(retained.path).mtimeMs, retained.modified);
      assert.deepEqual(readFileSync(retained.path), retained.bytes);
    }
    retained = {
      path: intermediate,
      modified: statSync(intermediate).mtimeMs,
      bytes: readFileSync(intermediate),
    };
    verifyRidNative(source, project, artifacts, configuration, name);
  }
}
