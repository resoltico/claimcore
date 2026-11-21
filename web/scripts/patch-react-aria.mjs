import { createHash } from "node:crypto";
import { mkdtempSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";

const version = "3.53.1";
/** @type {ReadonlyArray<readonly [extension: string, sha256: string]>} */
const originalModules = [
  ["cjs", "ff1b3b3d5d58e49c782165043b9cfa94d87b035b211f22f79a51a2b677ff9dea"],
  ["js", "2b2d677199bb877de2e3eb92be9c4a4300cf71a8d70f9b17739270e5ee00fc9b"],
  ["mjs", "8718978874d413b3abcc7114f18128342fef5ece7fa38388d59953cb059fd787"],
];

const moduleDirectory = "dist/private/live-announcer";
const changeNotice =
  "/*! ClaimCore modifies React Aria to clear live announcements without parsing HTML. */";
/** @type {ReadonlyArray<readonly [original: string, replacement: string]>} */
const edits = [
  ["this.assertiveLog.innerHTML = '';", `this.assertiveLog.replaceChildren(); ${changeNotice}`],
  ["this.politeLog.innerHTML = '';", "this.politeLog.replaceChildren();"],
];

export function patchReactAriaLiveAnnouncer() {
  const manifest = createRequire(import.meta.url).resolve("react-aria/package.json");
  const installed = JSON.parse(readFileSync(manifest, "utf8")).version;
  const review =
    "Review the React Aria LiveAnnouncer patch and the locked dependency before building.";
  if (installed !== version) {
    throw new Error(`Expected react-aria ${version}, found ${installed}. ${review}`);
  }

  const directory = join(dirname(manifest), moduleDirectory);
  // Validate every entry point before touching any of them. Accept our exact patched bytes too.
  const modules = originalModules.map(([extension, expected]) => {
    const name = `LiveAnnouncer.${extension}`;
    const path = join(directory, name);
    const source = readFileSync(path, "utf8");
    const original = edits.reduce((text, [before, after]) => text.replace(after, before), source);
    const digest = createHash("sha256").update(original).digest("hex");
    const patched = edits.reduce((text, [before, after]) => text.replace(before, after), original);
    if (digest !== expected || (source !== original && source !== patched)) {
      throw new Error(`Unexpected react-aria ${version} source in ${name}. ${review}`);
    }
    return { name, path, source, patched };
  });

  for (const { name, path, source, patched } of modules) {
    if (source === patched) {
      continue;
    }
    // Renaming on the same filesystem keeps simultaneous config loads from seeing partial files.
    const temporary = mkdtempSync(join(directory, ".claimcore-"));
    try {
      const replacement = join(temporary, name);
      writeFileSync(replacement, patched, { flag: "wx" });
      renameSync(replacement, path);
    } finally {
      rmSync(temporary, { recursive: true });
    }
  }
}

/** @param {{moduleIds: readonly string[]}} chunk */
export function reactAriaLiveAnnouncerBanner(chunk) {
  // Vite strips comments when minifying. Mark the emitted chunk containing the changed module.
  const source = `/react-aria/${moduleDirectory}/LiveAnnouncer.`;
  return chunk.moduleIds.some((id) => id.replaceAll("\\", "/").includes(source))
    ? changeNotice
    : "";
}
