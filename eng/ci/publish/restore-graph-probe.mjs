import { executable } from "../executable.mjs";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

/** @param {string} source @param {string[]} args @param {boolean} [accepted] */
function execute(source, args, accepted = true) {
  const result = spawnSync(executable("dotnet"), args, {
    cwd: source,
    encoding: "utf8",
    timeout: 600_000,
  });
  if (accepted) {
    assert.equal(result.status, 0, "Restored-graph positive control failed.");
  } else {
    assert.equal(result.status, 1);
    assert.match(result.stdout + result.stderr, /Producing inputs refused/u);
  }
}

/** @param {string} source @param {string} scratch */
export function verifyStaleRestore(source, scratch) {
  const project = "src/ClaimCore.Domain/ClaimCore.Domain.fsproj";
  const declaration = join(source, "Directory.Packages.props");
  const lock = join(source, "src/ClaimCore.Domain/packages.lock.json");
  const originalDeclaration = readFileSync(declaration, "utf8");
  const originalLock = readFileSync(lock);
  const version = JSON.parse(originalLock.toString()).dependencies["net10.0"]["FSharp.Core"]
    .resolved;
  const alternate = version === "10.1.400" ? "10.1.401" : "10.1.400";
  const obj = join(source, "artifacts/obj/ClaimCore.Domain");
  const retained = join(scratch, "retained-restore");
  mkdirSync(retained);
  const files = readdirSync(obj).filter(
    (path) => path.includes(".nuget.") || path === "project.assets.json",
  );
  for (const path of files) {
    copyFileSync(join(obj, path), join(retained, path));
  }
  writeFileSync(
    declaration,
    originalDeclaration.replace(
      `Include="FSharp.Core" Version="${version}"`,
      `Include="FSharp.Core" Version="${alternate}"`,
    ),
  );
  execute(source, ["restore", project, "--force-evaluate", "-p:RestoreLockedMode=false"]);
  for (const path of files) {
    copyFileSync(join(retained, path), join(obj, path));
  }
  execute(source, ["build", project, "--configuration", "Release", "--no-restore"], false);
  execute(source, ["restore", project, "--locked-mode"]);
  execute(source, ["build", project, "--configuration", "Release", "--no-restore"]);
  writeFileSync(declaration, originalDeclaration);
  writeFileSync(lock, originalLock);
  execute(source, ["restore", project, "--locked-mode"]);
}
