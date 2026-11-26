import { createHash } from "node:crypto";
import { readFile, writeFile, mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import { writeAttachmentViewer } from "./attachment-viewer.mjs";
import { messageShapes } from "../../contracts/messages.ts";
import { presentationLanguages, presentationPseudolanguage } from "../../contracts/client.ts";
import { chromeShapes } from "../src/presentation/chromeMessages.ts";
import { admitCatalog } from "../src/presentation/catalogAdmission.ts";
import { writeLegalAssets } from "./generate-third-party-notices.mjs";

const languages = presentationLanguages.filter((lang) => lang !== presentationPseudolanguage);
const coreSources = new URL(
  "../../src/ClaimCore.Presentation/Messages/Localization/",
  import.meta.url,
);
const chromeSources = new URL("../locales/", import.meta.url);
/** @param {Buffer} bytes */
const sha256 = (bytes) => createHash("sha256").update(bytes).digest("hex");

/** @param {URL} generated */
async function qualifiedCatalogs(generated) {
  const inventory = await readFile(new URL("catalog-manifest.json", generated), "utf8");
  const manifest = JSON.parse(inventory);
  const sources = JSON.parse(await readFile(new URL("catalog-sources.json", generated), "utf8"));
  if (Object.keys(manifest).sort().join(",") !== [...languages].sort().join(",")) {
    throw new Error("Prepared catalogs must cover every offered language");
  }
  const policy = await readFile(new URL("LocalizationPolicy.fs", coreSources));
  if (sha256(policy) !== sources.policy) {
    throw new Error("Prepared browser catalogs use a stale language policy. Run prepare:catalogs.");
  }
  const assets = await Promise.all(
    languages.map(async (language) => {
      const path = manifest[language];
      if (
        typeof path !== "string" ||
        /^\/assets\/catalogs\/([A-Za-z-]+)\.[0-9a-f]{64}\.json$/u.exec(path)?.[1] !== language
      ) {
        throw new Error(`Invalid prepared catalog path for ${language}`);
      }
      const fileName = path.slice(1);
      const [bytes, core, chrome] = await Promise.all([
        readFile(new URL(fileName, generated)),
        readFile(new URL(`${language}.json`, coreSources)),
        readFile(new URL(`${language}.json`, chromeSources)),
      ]);
      if (path !== `/assets/catalogs/${language}.${sha256(bytes)}.json`) {
        throw new Error(`Prepared catalog bytes do not match their path for ${language}`);
      }
      if (
        sources[`core/${language}`] !== sha256(core) ||
        sources[`chrome/${language}`] !== sha256(chrome)
      ) {
        throw new Error(
          `Prepared browser catalog sources are stale for ${language}. Run prepare:catalogs.`,
        );
      }
      const pair = JSON.parse(bytes.toString("utf8"));
      admitCatalog(pair.core, messageShapes);
      admitCatalog(pair.chrome, chromeShapes);
      return { fileName, bytes };
    }),
  );
  return { inventory, assets };
}

/** Inventory only actual bundler outputs, after their final bytes have been written.
 * @returns {import("vite").Plugin}
 */
export default function publicAssets() {
  const virtual = "virtual:claimcore-catalogs";
  const generated = new URL("../artifacts/catalogs/", import.meta.url);
  /** @type {ReturnType<typeof qualifiedCatalogs> | undefined} */
  let prepared;
  const loadCatalogs = () => (prepared ??= qualifiedCatalogs(generated));
  return {
    name: "claimcore-public-assets",
    enforce: "post",
    resolveId(id) {
      return id === virtual ? `\0${virtual}` : null;
    },
    async load(id) {
      return id === `\0${virtual}` ? `export default ${(await loadCatalogs()).inventory}` : null;
    },
    async writeBundle(options, bundle) {
      const directory = options.dir;
      if (directory === undefined) {
        throw new Error("Public assets require a build directory.");
      }
      const { inventory, assets: catalogs } = await loadCatalogs();
      await mkdir(resolve(directory, "assets/catalogs"), { recursive: true });
      await Promise.all(
        catalogs.map(({ fileName, bytes }) => writeFile(resolve(directory, fileName), bytes)),
      );
      await writeFile(resolve(directory, "catalog-manifest.json"), inventory);
      const legalFiles = writeLegalAssets(directory);
      const entries = [
        ...Object.values(bundle).filter((entry) => entry.fileName.startsWith("assets/")),
        { fileName: "react-aria-pressable.css" },
        { fileName: "styles.css" },
        { fileName: "brand.svg" },
        ...["index.html", "auth-recovery.html", ...legalFiles].map((fileName) => ({ fileName })),
        { fileName: "catalog-manifest.json" },
        ...catalogs,
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
        JSON.stringify({ schema: 4, assets, viewer }),
      );
    },
  };
}
