import { readFile, readdir } from "node:fs/promises";
import { resolve } from "node:path";
import { gzipSync } from "node:zlib";

const assets = resolve(import.meta.dirname, "../dist/assets");
const entries = await readdir(assets, { withFileTypes: true });
if (entries.some((entry) => !entry.isFile())) {
  throw new Error("Built Web assets must be regular files.");
}

const javascript = entries.filter((entry) => entry.name.endsWith(".js"));
const styles = entries.filter((entry) => entry.name.endsWith(".css"));
if (javascript.length === 0 || styles.length !== 1) {
  throw new Error("The Web build must contain JavaScript and exactly one stylesheet.");
}

const bytes = await Promise.all(javascript.map((entry) => readFile(resolve(assets, entry.name))));
const totalBytes = bytes.reduce((total, value) => total + value.byteLength, 0);
const totalGzipBytes = bytes.reduce(
  (total, value) => total + gzipSync(value, { level: 9 }).byteLength,
  0,
);
if (
  bytes.some((value) => value.byteLength > 600 * 1024) ||
  totalBytes > 900 * 1024 ||
  totalGzipBytes > 192 * 1024
) {
  throw new Error("The Web JavaScript bundle exceeds its reviewed byte budget.");
}

const style = await readFile(resolve(assets, styles[0].name));
if (style.byteLength > 64 * 1024) {
  throw new Error("The Web stylesheet exceeds its reviewed byte budget.");
}
