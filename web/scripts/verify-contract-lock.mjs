import { requireContractConsumption } from "./contract-promotion.mjs";
import { generatedDirectory } from "./contract-lock.mjs";

// Requires the generated contracts to match the committed lock. It reads only the lock and the files, so a
// job that merely received the artifacts can run it without installing the frontend dependencies.
await requireContractConsumption(generatedDirectory);
