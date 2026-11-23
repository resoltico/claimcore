import { createHash } from "node:crypto";
import { readFile, writeFile, readdir, mkdir, copyFile } from "node:fs/promises";
import { resolve } from "node:path";
import { writeAttachmentViewer } from "./attachment-viewer.mjs";
import { writeLegalAssets } from "./generate-third-party-notices.mjs";

/** Inventory only actual bundler outputs, after their final bytes have been written.
 * @returns {import("vite").Plugin}
 */
export default function publicAssets() {
  return {
    name: "claimcore-public-assets",
    enforce: "post",
    async writeBundle(options, bundle) {
      const directory = options.dir;
      if (directory === undefined) {
        throw new Error("Public assets require a build directory.");
      }
      const locales = await readdir(new URL("../locales/", import.meta.url));
      await mkdir(resolve(directory, "locales"), { recursive: true });
      const catalogFiles = locales.filter((name) => name.endsWith(".json"));
      await Promise.all(
        catalogFiles.map((name) =>
          copyFile(
            new URL(`../locales/${name}`, import.meta.url),
            resolve(directory, "locales", name),
          ),
        ),
      );
      const legalFiles = writeLegalAssets(directory);
      const entries = [
        ...Object.values(bundle).filter((entry) => entry.fileName.startsWith("assets/")),
        { fileName: "react-aria-pressable.css" },
        { fileName: "styles.css" },
        ...["index.html", "auth-recovery.html", ...legalFiles].map((fileName) => ({ fileName })),
        ...catalogFiles.map((name) => ({ fileName: `locales/${name}` })),
      ];
      const assets = await Promise.all(
        entries.map(async (entry) => {
          const bytes = await readFile(resolve(directory, entry.fileName));
          return {
            path: `/${entry.fileName}`,
            sha256: createHash("sha256").update(bytes).digest("hex"),
          };
        }),
      );
      assets.sort((left, right) => left.path.localeCompare(right.path));
      const viewer = await writeAttachmentViewer(directory);
      await writeFile(
        resolve(directory, "public-assets.json"),
        JSON.stringify({ schema: 2, assets, viewer }),
      );
    },
  };
}
