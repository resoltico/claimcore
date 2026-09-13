import { resolve } from "node:path";
import { generateConvergenceContracts } from "./contract-generation.mjs";

await generateConvergenceContracts(resolve(import.meta.dirname, "../src/generated/convergence"));
