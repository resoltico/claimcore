// Only the ignored isolated Docker build is modified; there is no production disable option.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { verifyTree, writeManifest } from "../publish/tree.mjs";

const source = "/mutation";
const publication = "/mutant";
const tree = join(publication, "database");
const manifest = join(publication, "manifests/database.json");
verifyTree("ClaimCore.Database", tree, manifest);
const policy = join(source, "src/ClaimCore.Witness/PostgresTransport.fs");
const original = readFileSync(policy, "utf8");
const enforcement = "builder.CheckCertificateRevocation <- true";
assert.equal(original.split(enforcement).length, 2);
writeFileSync(policy, original.replace(enforcement, "builder.CheckCertificateRevocation <- false"));
const build = spawnSync(
  "dotnet",
  [
    "build",
    "src/ClaimCore.Witness/ClaimCore.Witness.fsproj",
    "--configuration",
    "Release",
    "-p:RestoreLockedMode=true",
  ],
  {
    cwd: source,
    encoding: "utf8",
    timeout: 10 * 60 * 1000,
  },
);
assert.equal(
  build.status,
  0,
  "Isolated production-policy mutant must compile before its boundary experiment.",
);
const assembly = "ClaimCore.Witness.dll";
const output = join(source, "artifacts/bin/ClaimCore.Witness/release");
assert.notDeepEqual(readFileSync(join(tree, assembly)), readFileSync(join(output, assembly)));
for (const file of [assembly, "ClaimCore.Witness.pdb"]) {
  copyFileSync(join(output, file), join(tree, file));
}
rmSync(manifest);
writeManifest("ClaimCore.Database", tree, manifest);
verifyTree("ClaimCore.Database", tree, manifest);
