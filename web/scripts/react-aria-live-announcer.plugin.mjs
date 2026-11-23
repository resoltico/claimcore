import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";

// Upstream: https://github.com/adobe/react-spectrum/issues/11012
const version = "3.53.1";
/** @type {ReadonlyArray<readonly [extension: string, sha256: string]>} */
const originalModules = [
  ["cjs", "ff1b3b3d5d58e49c782165043b9cfa94d87b035b211f22f79a51a2b677ff9dea"],
  ["js", "2b2d677199bb877de2e3eb92be9c4a4300cf71a8d70f9b17739270e5ee00fc9b"],
  ["mjs", "8718978874d413b3abcc7114f18128342fef5ece7fa38388d59953cb059fd787"],
];

const moduleDirectory = "dist/private/live-announcer";
const changeNotice =
  "/*! ClaimCore modifies React Aria to clear live announcements without parsing HTML and to use external CSS. */";
const review =
  "Review the React Aria LiveAnnouncer transform and the locked dependency. Run npm ci to replace an older patched installation.";

/** @param {string} id */
const originalModule = (id) => {
  if (id.startsWith("\0")) {
    return undefined;
  }
  const [path] = id.replaceAll("\\", "/").split("?");
  const match = originalModules.find(([extension]) =>
    path?.endsWith(`/node_modules/react-aria/${moduleDirectory}/LiveAnnouncer.${extension}`),
  );
  return path !== undefined && match !== undefined
    ? { path, extension: match[0], sha256: match[1] }
    : undefined;
};

/** @param {string} source @param {string} extension @param {string} expected */
const checkOriginal = (source, extension, expected) => {
  if (createHash("sha256").update(source).digest("hex") !== expected) {
    throw new Error(
      `Unexpected react-aria ${version} source in LiveAnnouncer.${extension}. ${review}`,
    );
  }
};

/** @returns {import("vite").Plugin} */
export default function reactAriaLiveAnnouncer() {
  return {
    name: "claimcore-react-aria-live-announcer",
    enforce: "pre",
    buildStart() {
      // Detect package layout changes even when an updated package no longer loads these modules.
      const manifest = createRequire(import.meta.url).resolve("react-aria/package.json");
      const installed = JSON.parse(readFileSync(manifest, "utf8")).version;
      if (installed !== version) {
        throw new Error(`Expected react-aria ${version}, found ${installed}. ${review}`);
      }
      const directory = join(dirname(manifest), moduleDirectory);
      for (const [extension, expected] of originalModules) {
        const source = readFileSync(join(directory, `LiveAnnouncer.${extension}`), "utf8");
        checkOriginal(source, extension, expected);
      }
    },
    transform(source, id) {
      const original = originalModule(id);
      if (original === undefined) {
        return null;
      }
      const upstream = readFileSync(original.path, "utf8");
      checkOriginal(upstream, original.extension, original.sha256);
      // The Vite dev loader blanks source map directives before calling transform.
      const directive = `//# sourceMappingURL=LiveAnnouncer.${original.extension}.map`;
      if (
        source !== upstream &&
        source !== upstream.replace(directive, " ".repeat(directive.length))
      ) {
        throw new Error(
          `Unexpected transform input in LiveAnnouncer.${original.extension}. ${review}`,
        );
      }
      return {
        code: source
          .replace(
            "this.assertiveLog.innerHTML = '';",
            `this.assertiveLog.replaceChildren(); ${changeNotice}`,
          )
          .replace(/Object\.assign\(this\.node\.style, \{[\s\S]*?\}\);/u, "")
          .replace("this.politeLog.innerHTML = '';", "this.politeLog.replaceChildren();"),
        map: null,
      };
    },
  };
}

/** @param {{moduleIds: readonly string[]}} chunk */
export function reactAriaLiveAnnouncerBanner(chunk) {
  // Minification can remove source comments. Mark the chunk that contains the modified module.
  return chunk.moduleIds.some((id) => originalModule(id) !== undefined) ? changeNotice : "";
}
