import { copyFileSync, existsSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";

// Called only after the completed fixture's sensitive-output scan succeeds.
const [source, destination] = process.argv.slice(2);
if (source === undefined || destination === undefined || process.argv.length !== 4) {
  throw new Error("Pass the scanned diagnostic directory and a new private failure directory.");
}
mkdirSync(dirname(destination), { mode: 0o700, recursive: true });
mkdirSync(destination, { mode: 0o700 });
for (const file of ["web-host.log", "playwright.log"]) {
  const path = join(source, file);
  if (existsSync(path)) {
    copyFileSync(path, join(destination, file));
  }
}
