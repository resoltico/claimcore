import { readdir, readFile } from "node:fs/promises";
import { join } from "node:path";

const sourceDirectory = new URL("../src", import.meta.url);
const entries = await readdir(sourceDirectory, { recursive: true });
const cssFiles = entries.filter((entry) => entry.endsWith(".css"));

for (const file of cssFiles) {
  const path = join(sourceDirectory.pathname, file);
  const lines = (await readFile(path, "utf8")).split("\n").length;
  if (lines > 300) throw new Error(`${file} has ${lines} physical lines; maximum is 300.`);
}
