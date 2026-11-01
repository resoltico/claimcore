import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";

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
      const entries = Object.values(bundle).filter((entry) => entry.fileName.startsWith("assets/"));
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
      await writeFile(
        resolve(directory, "public-assets.json"),
        JSON.stringify({ schema: 1, assets }),
      );
    },
  };
}
