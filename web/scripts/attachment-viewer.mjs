import { createHash } from "node:crypto";
import { readFile, readdir, writeFile } from "node:fs/promises";
import { basename, extname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "vite";

const viewerEntry = fileURLToPath(new URL("../src/features/case/pdf/viewer.ts", import.meta.url));
const contracts = fileURLToPath(new URL("../../contracts", import.meta.url));
const pdfjs = new URL("../node_modules/pdfjs-dist/", import.meta.url);
const resourceModule = "virtual:attachment-preview-resources";
const resolvedModule = `\0${resourceModule}`;
const workerModule = "virtual:attachment-preview-worker";
const resolvedWorkerModule = `\0${workerModule}`;
const workerEntry = fileURLToPath(new URL("build/pdf.worker.min.mjs", pdfjs));

const wasm = ["openjpeg.wasm", "jbig2.wasm"];

export const decoderLicenses = [
  new URL("cmaps/LICENSE", pdfjs),
  ...["OPENJPEG", "PDFJS_OPENJPEG", "JBIG2", "PDFJS_JBIG2"].map(
    (name) => new URL(`wasm/LICENSE_${name}`, pdfjs),
  ),
];

/** @returns {import("vite").Plugin} */
export const attachmentPreviewResources = () => {
  let building = false;
  return {
    name: "claimcore-attachment-preview-resources",
    configResolved(config) {
      building = config.command === "build";
    },
    resolveId(id) {
      return id === resourceModule ? resolvedModule : null;
    },
    async load(id) {
      if (id !== resolvedModule) {
        return null;
      }
      const cmaps = (await readdir(new URL("cmaps/", pdfjs))).filter((name) =>
        name.endsWith(".bcmap"),
      );
      /** @type {Record<string, Record<string, string>>} */
      const resources = {};
      for (const [kind, names] of Object.entries({ cmaps, wasm })) {
        const entries = [];
        for (const name of names) {
          const source = await readFile(new URL(`${kind}/${name}`, pdfjs));
          const hash = createHash("sha256").update(source).digest("hex");
          const extension = extname(name);
          const fileName = `assets/pdf-${basename(name, extension)}-${hash.slice(0, 16)}${extension}`;
          if (building) {
            this.emitFile({ type: "asset", fileName, source });
          }
          entries.push([name, `/${fileName}`]);
        }
        resources[kind] = Object.fromEntries(entries);
      }
      return `export default ${JSON.stringify(resources)};`;
    },
  };
};

/**
 * @param {string} entry
 * @param {string} name
 * @param {import("vite").Plugin[]} plugins
 */
const classicScript = async (entry, name, plugins = []) => {
  const output = await build({
    configFile: false,
    // Browser decoding uses the port factories, so the classic script has no module URL.
    define: { "import.meta": "{}" },
    plugins,
    resolve: { alias: { "@contracts": contracts } },
    build: {
      write: false,
      target: "es2022",
      lib: { entry, name, formats: ["iife"] },
      rolldownOptions: { output: { codeSplitting: false } },
    },
    logLevel: "warn",
  });
  const result = Array.isArray(output) && output.length === 1 ? output[0] : output;
  if (
    result === undefined ||
    Array.isArray(result) ||
    !("output" in result) ||
    result.output.length !== 1
  ) {
    throw new Error("The attachment viewer must build to exactly one output.");
  }
  const [bundle] = result.output;
  if (bundle?.type !== "chunk" || bundle.imports.length > 0 || bundle.dynamicImports.length > 0) {
    throw new Error("The attachment viewer script may not load subresources.");
  }
  return bundle.code.replaceAll("\r\n", "\n");
};

/** @param {string} directory */
export const writeAttachmentViewer = async (directory) => {
  // Chromium's opaque-origin renderer cannot load a module blob worker.
  const worker = await classicScript(workerEntry, "ClaimCorePdfWorker");
  const script = await classicScript(viewerEntry, "ClaimCoreAttachmentViewer", [
    {
      name: "claimcore-attachment-preview-worker",
      resolveId(id) {
        return id === workerModule ? resolvedWorkerModule : null;
      },
      load(id) {
        return id === resolvedWorkerModule ? `export default ${JSON.stringify(worker)};` : null;
      },
    },
  ]);
  if (/<\/script/iu.test(script)) {
    throw new Error("The attachment viewer script contains a closing tag.");
  }
  const html =
    '<!doctype html><html><head><meta charset="utf-8"></head><body>' +
    '<svg display="block" width="1" height="1"><foreignObject><canvas></canvas></foreignObject></svg>' +
    `<script>${script}</script></body></html>`;
  await writeFile(resolve(directory, "attachment-preview.viewer"), html);
  return {
    sha256: createHash("sha256").update(html).digest("hex"),
    scriptSha256: createHash("sha256").update(script).digest("base64"),
  };
};
