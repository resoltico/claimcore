import { readFile, readdir, rm } from "node:fs/promises";
import { mkdtemp } from "node:fs/promises";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";
import { generateConvergenceContracts } from "./contract-generation.mjs";
import { maximumStandaloneValidatorBytes } from "./generate-web-validators.mjs";

const root = resolve(import.meta.dirname, "../..");
const generated = resolve(root, "web/src/generated/convergence");

const manifestFiles = async (directory) => {
  const manifestPath = join(directory, "convergence-manifest.json");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
  const files = manifest.files;
  if (
    !Array.isArray(files) ||
    files.length === 0 ||
    !files.every((file) => typeof file === "string")
  ) {
    throw new Error("Generated convergence manifest has an invalid file inventory.");
  }
  const expected = [...files, "convergence-manifest.json"].sort();
  if (new Set(expected).size !== expected.length) {
    throw new Error("Generated convergence manifest names a file more than once.");
  }
  return expected;
};

const actualFiles = async (directory) => {
  const entries = await readdir(directory, { withFileTypes: true });
  if (entries.some((entry) => !entry.isFile())) {
    throw new Error("Generated convergence output must contain files only.");
  }
  return entries.map((entry) => entry.name).sort();
};

const sameInventory = (left, right) =>
  left.length === right.length && left.every((value, index) => value === right[index]);

const differs = async (output, file) => {
  const checkedIn = await readFile(join(generated, file));
  const regenerated = await readFile(join(output, file));
  return !checkedIn.equals(regenerated);
};

const assertValidatorSize = async (directory) => {
  const validators = await readFile(join(directory, "web-v2.validators.mjs"));
  if (validators.byteLength > maximumStandaloneValidatorBytes) {
    throw new Error("Generated Web validators exceed their one-megabyte ceiling.");
  }
};

const protocol = resolve(root, "src/ClaimCore.Protocol/Generated");
const protocolOutput = await mkdtemp(join(tmpdir(), "claimcore-protocol-"));

const output = await mkdtemp(join(tmpdir(), "claimcore-convergence-contracts-"));
try {
  await generateConvergenceContracts(output, protocolOutput);
  const protocolFiles = await actualFiles(protocolOutput);
  if (!sameInventory(await actualFiles(protocol), protocolFiles))
    throw new Error("Generated .NET protocol file inventory is stale.");
  for (const file of protocolFiles) {
    const actual = await readFile(join(protocol, file));
    const expected = await readFile(join(protocolOutput, file));
    if (!actual.equals(expected)) throw new Error(`Generated .NET protocol is stale: ${file}.`);
  }
  await assertValidatorSize(output);
  const expected = await manifestFiles(output);
  const generatedFiles = await actualFiles(generated);
  if (!sameInventory(generatedFiles, expected)) {
    throw new Error("Checked-in convergence contract files do not match the generated inventory.");
  }
  const changed = await Promise.all(
    expected.map(async (file) => ((await differs(output, file)) ? file : null)),
  );
  const stale = changed.filter((file) => file !== null);
  if (stale.length > 0)
    throw new Error(`Generated convergence contract is stale: ${stale.join(", ")}.`);
} finally {
  await rm(output, { force: true, recursive: true });
  await rm(protocolOutput, { force: true, recursive: true });
}
