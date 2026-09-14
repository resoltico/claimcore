import { copyFile, mkdir, mkdtemp, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { generateConvergenceContracts } from "./contract-generation.mjs";

const destination = resolve(import.meta.dirname, "../../src/ClaimCore.Protocol/Generated");
const temporary = await mkdtemp(join(tmpdir(), "claimcore-protocol-"));
const owned =
  /^(?:(?:Types|Scalar|Codec|Binding)[0-9]{3}\.fs|Definition\.fs|WebV2\.fs|Protocol\.Generated\.props)$/;
try {
  await generateConvergenceContracts(
    resolve(import.meta.dirname, "../src/generated/convergence"),
    temporary,
  );
  await mkdir(destination, { recursive: true });
  const existing = await readdir(destination, { withFileTypes: true });
  if (existing.some((entry) => !entry.isFile() || !owned.test(entry.name)))
    throw new Error("Refusing to overwrite unknown files in the generated protocol directory.");
  const names = await readdir(temporary);
  if (names.some((name) => !owned.test(name)))
    throw new Error("Unknown generated protocol output.");
  for (const entry of existing)
    if (!names.includes(entry.name)) await rm(join(destination, entry.name));
  for (const name of names) await copyFile(join(temporary, name), join(destination, name));
} finally {
  await rm(temporary, { recursive: true, force: true });
}
