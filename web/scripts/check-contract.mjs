import { generatedDirectory, verifyLock } from "./contract-lock.mjs";
import { assertValidatorSizes } from "./validator-sizes.mjs";

// Requires the generated contracts to be present and to match the committed lock; it never regenerates.
await assertValidatorSizes(generatedDirectory);
await verifyLock(generatedDirectory);
