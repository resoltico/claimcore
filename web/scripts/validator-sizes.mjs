import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import { maximumStandaloneValidatorGroupBytes } from "./generate-web-validators.mjs";
import { standaloneValidatorArtifacts } from "./validator-groups.mjs";

/** @param {string[]} left @param {string[]} right */
const sameInventory = (left, right) =>
  left.length === right.length && left.every((value, index) => value === right[index]);

/**
 * The generated validator groups must be exactly the declared groups, each under its size ceiling.
 * @param {string} directory
 */
export const assertValidatorSizes = async (directory) => {
  const entries = await readdir(directory, { withFileTypes: true });
  const validators = entries
    .filter((entry) => /^web-v3\.validators\.[a-z]+\.mjs$/u.test(entry.name))
    .map((entry) => entry.name)
    .sort();
  const expected = standaloneValidatorArtifacts
    .filter((name) => /^web-v3\.validators\.[a-z]+\.mjs$/u.test(name))
    .sort();
  if (!sameInventory(validators, expected)) {
    throw new Error("Generated Web validators must match the declared group inventory.");
  }
  for (const name of validators) {
    const source = await readFile(join(directory, name));
    if (source.byteLength > maximumStandaloneValidatorGroupBytes) {
      throw new Error(`Generated Web validator group ${name} exceeds its 600 KiB ceiling.`);
    }
  }
};
