import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { resolve, sep } from "node:path";

import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";
import { default as standaloneCode } from "ajv/dist/standalone/index.js";
import { rolldown } from "rolldown";

const createCompiler = () => {
  const compiler = new Ajv2020({
    allErrors: false,
    code: { esm: true, lines: true, optimize: 2, source: true },
    coerceTypes: false,
    inlineRefs: false,
    loopEnum: 5,
    loopRequired: 5,
    messages: false,
    ownProperties: true,
    removeAdditional: false,
    strict: true,
    useDefaults: false,
    validateFormats: true,
    verbose: false,
  });
  addFormats(compiler);
  return compiler;
};

const embeddedPackages = (moduleIds, webDirectory, temporary) => {
  const marker = `${sep}node_modules${sep}`;
  const names = new Set(["rolldown"]);
  for (const moduleId of moduleIds) {
    if (resolve(moduleId).startsWith(`${temporary}${sep}`)) continue;
    const markerIndex = moduleId.lastIndexOf(marker);
    if (markerIndex < 0) continue;
    const dependency = moduleId.slice(markerIndex + marker.length);
    const parts = dependency.split(sep);
    const name = parts[0]?.startsWith("@") ? `${parts[0]}/${parts[1]}` : parts[0];
    const dependenciesRoot = `${resolve(webDirectory, "node_modules")}${sep}`;
    if (name !== undefined && resolve(moduleId).startsWith(dependenciesRoot)) {
      names.add(name);
    }
  }
  return [...names].sort();
};

const validateSchemas = (schemas) => {
  const identifiers = new Set();
  for (const schema of schemas) {
    const identifier = schema?.["$id"];
    if (typeof identifier !== "string" || identifier.length === 0 || identifiers.has(identifier)) {
      throw new Error("Validator schema identifiers must be present and unique.");
    }
    identifiers.add(identifier);
  }
  if (schemas.length === 0) throw new Error("At least one validator schema is required.");
};

const validateExports = (validators) => {
  const names = new Set();
  const references = new Set();
  for (const validator of validators) {
    if (!/^[A-Za-z_$][A-Za-z0-9_$]*$/u.test(validator.exportName)) {
      throw new Error("Validator export names must be JavaScript identifiers.");
    }
    if (
      typeof validator.schemaReference !== "string" ||
      validator.schemaReference.length === 0 ||
      names.has(validator.exportName) ||
      references.has(validator.schemaReference)
    ) {
      throw new Error("Validator export names and schema references must be present and unique.");
    }
    names.add(validator.exportName);
    references.add(validator.schemaReference);
  }
  if (validators.length === 0) throw new Error("At least one validator export is required.");
};

const bundledCode = async (source, webDirectory) => {
  const cache = resolve(webDirectory, "node_modules/.cache");
  await mkdir(cache, { recursive: true });
  const temporary = await mkdtemp(resolve(cache, "claimcore-validator-"));
  const entry = resolve(temporary, "entry.mjs");
  try {
    await writeFile(entry, source, "utf8");
    const bundle = await rolldown({ input: entry, treeshake: true });
    const generated = await bundle.generate({ format: "esm", minify: true, sourcemap: false });
    const chunks = generated.output.filter((item) => item.type === "chunk");
    if (
      chunks.length !== 1 ||
      generated.output.length !== 1 ||
      chunks[0].imports.length !== 0 ||
      chunks[0].dynamicImports.length !== 0
    ) {
      throw new Error("Standalone validator bundling must produce one self-contained ESM chunk.");
    }
    if (chunks[0].code.includes(temporary)) {
      throw new Error("Standalone validator output contains a temporary path.");
    }
    const header = "/* Generated from ClaimCore.Contracts schemas. Do not edit. */";
    return {
      embeddedPackages: embeddedPackages(chunks[0].moduleIds, webDirectory, temporary),
      source: `${header}${chunks[0].code.trim()}\n`,
    };
  } finally {
    await rm(temporary, { force: true, recursive: true });
  }
};

export const compileStandaloneValidators = async (inventory, webDirectory) => {
  validateSchemas(inventory.schemas);
  validateExports(inventory.validators);
  const compiler = createCompiler();
  for (const schema of inventory.schemas) compiler.addSchema(schema);
  for (const validator of inventory.validators) {
    if (compiler.getSchema(validator.schemaReference) === undefined) {
      throw new Error(`Validator ${validator.exportName} refers to an unknown schema.`);
    }
  }
  const exports = Object.fromEntries(
    inventory.validators.map((validator) => [validator.exportName, validator.schemaReference]),
  );
  return bundledCode(standaloneCode(compiler, exports), webDirectory);
};
