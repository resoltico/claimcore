import { resolve } from "node:path";
import { generateContracts } from "./contract-generation.mjs";
import { generatedDirectory } from "./contract-lock.mjs";
import { assertValidatorSizes } from "./validator-sizes.mjs";
import { promoteContracts } from "./contract-promotion.mjs";

if (process.argv.some((argument) => argument.startsWith("--") && argument !== "--write-lock")) {
  throw new Error(
    "Only intentional --write-lock is supported. Interrupted writers require the preservation procedure in web/README.md.",
  );
}

await promoteContracts({
  directory: generatedDirectory,
  lockFile: resolve(import.meta.dirname, "../../config/contracts.lock.json"),
  generate: generateContracts,
  validate: assertValidatorSizes,
  acceptLock: process.argv.includes("--write-lock"),
});
