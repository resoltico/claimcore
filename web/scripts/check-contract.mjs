import { readFile, readdir, rm } from "node:fs/promises";
import { mkdtemp } from "node:fs/promises";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";
import { generateConvergenceContracts } from "./contract-generation.mjs";
import { maximumStandaloneValidatorGroupBytes } from "./generate-web-validators.mjs";
import { standaloneValidatorArtifacts } from "./validator-groups.mjs";

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

const assertValidatorSizes = async (directory) => {
  const entries = await readdir(directory, { withFileTypes: true });
  const validators = entries
    .filter((entry) => /^web-v2\.validators\.[a-z]+\.mjs$/u.test(entry.name))
    .map((entry) => entry.name)
    .sort();
  const expected = standaloneValidatorArtifacts
    .filter((name) => /^web-v2\.validators\.[a-z]+\.mjs$/u.test(name))
    .sort();
  if (!sameInventory(validators, expected))
    throw new Error("Generated Web validators must match the declared group inventory.");
  for (const name of validators) {
    const source = await readFile(join(directory, name));
    if (source.byteLength > maximumStandaloneValidatorGroupBytes) {
      throw new Error(`Generated Web validator group ${name} exceeds its 600 KiB ceiling.`);
    }
  }
};

const output = await mkdtemp(join(tmpdir(), "claimcore-convergence-contracts-"));
try {
  await generateConvergenceContracts(output);
  await assertValidatorSizes(output);
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
}
