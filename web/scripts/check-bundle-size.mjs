import { readFile, readdir } from "node:fs/promises";
import { resolve } from "node:path";
import { gzipSync } from "node:zlib";

const dist = resolve(import.meta.dirname, "../dist");
const assets = resolve(dist, "assets");
const entries = await readdir(assets, { withFileTypes: true });
if (entries.some((entry) => !entry.isFile())) {
  throw new Error("Built Web assets must be regular files.");
}

const javascript = entries.filter((entry) => entry.name.endsWith(".js"));
const styles = entries.filter((entry) => entry.name.endsWith(".css"));
if (javascript.length === 0 || styles.length !== 1) {
  throw new Error("The Web build must contain JavaScript and exactly one stylesheet.");
}

const sources = new Map(
  await Promise.all(
    javascript.map(async (entry) => [
      entry.name,
      await readFile(resolve(assets, entry.name), "utf8"),
    ]),
  ),
);

const entryScripts = (html) =>
  Array.from(
    html.matchAll(
      /<script\b(?=[^>]*\btype=["']module["'])(?=[^>]*\bsrc=["']\/assets\/([^"']+\.js)["'])[^>]*><\/script>/gu,
    ),
    (match) => match[1],
  );

const staticImports = (source) =>
  Array.from(
    source.matchAll(/(?:^|;)import(?:[^"']*?from)?["']\.\/([^"']+\.js)["']/gu),
    (match) => match[1],
  );

const initialEntrySources = (scripts) => {
  const pending = [...scripts];
  const initial = new Set();
  while (pending.length > 0) {
    const name = pending.pop();
    if (name === undefined || initial.has(name)) continue;
    const source = sources.get(name);
    if (source === undefined)
      throw new Error("The Web entry references an absent JavaScript chunk.");
    initial.add(name);
    pending.push(...staticImports(source));
  }
  return initial;
};

const html = await readFile(resolve(dist, "index.html"), "utf8");
const initialSources = initialEntrySources(entryScripts(html));
if (initialSources.size === 0) throw new Error("The Web build has no module entry script.");

const bytes = [...sources.values()].map((source) => Buffer.from(source));
const initialBytes = [...initialSources].reduce(
  (total, name) => total + Buffer.byteLength(sources.get(name) ?? ""),
  0,
);
const totalGzipBytes = bytes.reduce(
  (total, value) => total + gzipSync(value, { level: 9 }).byteLength,
  0,
);
if (
  bytes.some((value) => value.byteLength > 600 * 1024) ||
  initialBytes > 900 * 1024 ||
  totalGzipBytes > 192 * 1024
) {
  throw new Error("The Web JavaScript bundle exceeds its reviewed byte budget.");
}

const style = await readFile(resolve(assets, styles[0].name));
if (style.byteLength > 64 * 1024) {
  throw new Error("The Web stylesheet exceeds its reviewed byte budget.");
}
