import { rm } from "node:fs/promises";
import { generateContracts } from "./contract-generation.mjs";
import { generatedDirectory, verifyLock, writeLock } from "./contract-lock.mjs";
import { assertValidatorSizes } from "./validator-sizes.mjs";

// Generates every contract artifact from the F# projection, then either accepts the result as the new
// lock (`--write-lock`, an explicit maintainer decision) or requires it to match the committed lock.
await rm(generatedDirectory, { force: true, recursive: true });
await generateContracts(generatedDirectory);
await assertValidatorSizes(generatedDirectory);
if (process.argv.includes("--write-lock")) {
  await writeLock(generatedDirectory);
} else {
  await verifyLock(generatedDirectory);
}
