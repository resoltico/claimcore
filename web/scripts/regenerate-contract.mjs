import { rm, mkdir } from "node:fs/promises";
import { generateContracts } from "./contract-generation.mjs";
import { generatedDirectory, verifyLock, writeLock } from "./contract-lock.mjs";

const args = process.argv.slice(2);
if (args.length > 1 || (args.length === 1 && args[0] !== "--write-lock")) {
  throw new Error("Use no arguments, or --write-lock for an intentional contract change.");
}
await rm(generatedDirectory, { recursive: true, force: true });
await mkdir(generatedDirectory, { recursive: true });
await generateContracts(generatedDirectory);
if (args.includes("--write-lock")) {
  await writeLock(generatedDirectory);
}
await verifyLock(generatedDirectory);
