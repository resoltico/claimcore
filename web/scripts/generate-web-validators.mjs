import { packageVersion, validatorNotice } from "./validator-notices.mjs";
import { readFile, readdir, rename, rm, writeFile } from "node:fs/promises";
import { basename, join, resolve } from "node:path";

import { format, resolveConfig } from "prettier";

import { compileStandaloneValidators } from "./standalone-validators.mjs";
import {
  obsoleteStandaloneValidatorArtifacts,
  standaloneValidatorArtifacts,
  validationWrapper,
  validatorDeclarations,
  validatorGroups,
  validatorName,
} from "./validator-groups.mjs";

const hostSchema = "web-v3.host-failure.schema.json";
const responsesSchema = "web-v3.responses.schema.json";
const safeName = /^[A-Za-z0-9][A-Za-z0-9._-]*$/u;
export const maximumStandaloneValidatorGroupBytes = 600 * 1024;

/** @param {string} file @returns {Promise<import("./tooling-types.mjs").JsonRecord>} */
const readJson = async (file) => JSON.parse(await readFile(file, "utf8"));
/** @param {string} source @param {string} filepath */
const formatTypeScript = async (source, filepath) => {
  const canonicalPath = resolve(
    import.meta.dirname,
    "../src/generated/contracts",
    basename(filepath),
  );
  const configuration = await resolveConfig(canonicalPath, { editorconfig: true });
  if (configuration === null) {
    throw new Error("The Web formatting configuration is unavailable.");
  }
  return format(source, { ...configuration, filepath: canonicalPath });
};

/** @param {import("./tooling-types.mjs").JsonRecord} catalog @param {Set<string>} provisional */
const endpointInventory = (catalog, provisional) => {
  if (!Array.isArray(catalog["endpoints"]) || catalog["endpoints"].length === 0) {
    throw new Error("The Web v3 catalog has no endpoint inventory.");
  }
  const seen = new Set();
  return catalog["endpoints"].map(
    (/** @type {{id: string, responseDefinition: string}} */ endpoint) => {
      const { id, responseDefinition } = endpoint;
      if (
        typeof id !== "string" ||
        seen.has(id) ||
        typeof responseDefinition !== "string" ||
        !safeName.test(responseDefinition)
      ) {
        throw new Error("The Web v3 endpoint inventory is invalid.");
      }
      const responseSchema = `web-v3.endpoint.${id}.response.schema.json`;
      if (
        !safeName.test(responseSchema) ||
        basename(responseSchema) !== responseSchema ||
        !provisional.has(responseSchema)
      ) {
        throw new Error("The Web v3 response-schema inventory is invalid.");
      }
      seen.add(id);
      return { endpoint: id, exportName: validatorName(id), responseDefinition };
    },
  );
};

/** @param {string} directory @param {string} file @returns {Promise<import("ajv").AnySchemaObject>} */
const readSchema = async (directory, file) => {
  const schema = await readJson(join(directory, file));
  const identifier = `https://claimcore.local/contracts/${file}`;
  if (
    schema?.["$schema"] !== "https://json-schema.org/draft/2020-12/schema" ||
    schema?.["$id"] !== identifier
  ) {
    throw new Error(`Generated schema ${file} has an invalid dialect or identifier.`);
  }
  return schema;
};

/** @param {string} directory @param {import("./tooling-types.mjs").ValidatorEndpoint[]} endpoints @param {string} group */
const validatorInventory = async (directory, endpoints, group) => {
  if (group === "host") {
    const host = await readSchema(directory, hostSchema);
    if (typeof host.$id !== "string") {
      throw new Error("Host schema needs an identifier.");
    }
    return {
      schemas: [host],
      validators: [{ exportName: "validate_host_failure", schemaReference: host.$id }],
    };
  }
  const aggregate = await readSchema(directory, responsesSchema);
  const endpointValidators = endpoints.map(({ exportName, responseDefinition }) => {
    if (
      responseDefinition === undefined ||
      aggregate["$defs"]?.[responseDefinition] === undefined
    ) {
      throw new Error(`Aggregate responses omit ${responseDefinition}.`);
    }
    return {
      exportName,
      schemaReference: `${aggregate.$id}#/$defs/${responseDefinition}`,
    };
  });
  return {
    schemas: [aggregate],
    validators: endpointValidators,
  };
};

/** @param {string} directory @param {Set<string>} provisional */
const assertProvisionalOutput = async (directory, provisional) => {
  const entries = await readdir(directory, { withFileTypes: true });
  if (entries.some((entry) => !entry.isFile())) {
    throw new Error("Generated contract output must contain regular files only.");
  }
  const allowed = new Set([
    ...provisional,
    "contracts-manifest.json",
    ...standaloneValidatorArtifacts,
    ...obsoleteStandaloneValidatorArtifacts,
  ]);
  const extra = entries.map((entry) => entry.name).filter((name) => !allowed.has(name));
  const missing = [...provisional].filter((name) => !entries.some((entry) => entry.name === name));
  if (extra.length > 0 || missing.length > 0) {
    throw new Error("The provisional contract artifact inventory is incomplete or has extras.");
  }
};

/** @param {string} file @param {string} contents */
const atomicWrite = async (file, contents) => {
  const temporary = `${file}.${process.pid}.tmp`;
  try {
    await writeFile(temporary, contents, { encoding: "utf8", mode: 0o644 });
    await rm(file, { force: true });
    await rename(temporary, file);
  } finally {
    await rm(temporary, { force: true });
  }
};

/** @param {string} directory @param {Set<string>} provisional */
const formatTypeScriptArtifacts = async (directory, provisional) => {
  const names = [...provisional]
    .filter(
      (name) =>
        /^web-v3\.endpoint-catalog(?:\.[a-z]+)?\.ts$/u.test(name) ||
        /^web-v3\.types(?:\.[a-z]+)*\.ts$/u.test(name),
    )
    .sort();
  if (names.length < 2) {
    throw new Error("Generated Web TypeScript artifacts are missing.");
  }
  for (const name of names) {
    const file = join(directory, name);
    const source = await readFile(file, "utf8");
    await atomicWrite(file, await formatTypeScript(source, file));
  }
};

/** @param {import("./tooling-types.mjs").JsonRecord} manifest @param {Set<string>} provisional @param {import("./tooling-types.mjs").PackageLock} lock @param {string[]} embeddedPackages */
const combinedManifest = (manifest, provisional, lock, embeddedPackages) => ({
  ...manifest,
  files: [...provisional, ...standaloneValidatorArtifacts].sort(),
  ajv: {
    version: packageVersion(lock, "ajv"),
    formatsVersion: packageVersion(lock, "ajv-formats"),
    bundler: `rolldown@${packageVersion(lock, "rolldown")}`,
    embeddedPackages: embeddedPackages.map((name) => `${name}@${packageVersion(lock, name)}`),
  },
});

/** @param {string} source @param {{exportName: string}[]} entries */
const assertValidatorModule = async (source, entries) => {
  const url = `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`;
  const compiled = await import(url);
  const expected = entries.map((entry) => entry.exportName).sort();
  const actual = Object.keys(compiled).sort();
  if (actual.length !== expected.length || actual.some((name, index) => name !== expected[index])) {
    throw new Error("Standalone validator exports do not match the schema inventory.");
  }
  if (expected.some((name) => typeof compiled[name] !== "function" || compiled[name](undefined))) {
    throw new Error("A standalone validator export is invalid or accepts an absent envelope.");
  }
};

/** @param {string} output */
const provisionalInventory = async (output) => {
  const manifestPath = join(output, "contracts-manifest.json");
  const manifest = await readJson(manifestPath);
  if (
    !Array.isArray(manifest["files"]) ||
    !manifest["files"].every((file) => safeName.test(file))
  ) {
    throw new Error("The provisional contract manifest is invalid.");
  }
  const provisional = new Set(manifest["files"]);
  if (
    provisional.size !== manifest["files"].length ||
    standaloneValidatorArtifacts.some((artifact) => provisional.has(artifact))
  ) {
    throw new Error("The F# manifest must contain a unique provisional artifact inventory.");
  }
  if (!provisional.has(hostSchema)) {
    throw new Error("The host-failure schema is missing.");
  }
  if (!provisional.has(responsesSchema)) {
    throw new Error("The aggregate response schema is missing.");
  }
  await assertProvisionalOutput(output, provisional);
  return { manifest, provisional };
};

/** @param {string} output @param {import("./tooling-types.mjs").ValidatorGroups} groups @param {string} webDirectory */
const compileGroups = (output, groups, webDirectory) =>
  Promise.all(
    Object.entries(groups).map(async ([group, groupEndpoints]) => {
      const inventory = await validatorInventory(output, groupEndpoints, group);
      const compiled = await compileStandaloneValidators(inventory, webDirectory);
      if (Buffer.byteLength(compiled.source) > maximumStandaloneValidatorGroupBytes) {
        throw new Error(`The ${group} standalone validator group exceeds the 600 KiB ceiling.`);
      }
      await assertValidatorModule(compiled.source, inventory.validators);
      return { group, compiled };
    }),
  );

/** @param {string} output @param {import("./tooling-types.mjs").ValidatorGroups} groups @param {Awaited<ReturnType<typeof compileGroups>>} compiledGroups */
const writeValidatorGroups = async (output, groups, compiledGroups) => {
  await Promise.all(
    compiledGroups.map(async ({ group, compiled }) => {
      const declarationFile = join(output, `web-v3.validators.${group}.d.mts`);
      const declarationSource = await formatTypeScript(
        validatorDeclarations(
          groups[/** @type {keyof import("./tooling-types.mjs").ValidatorGroups} */ (group)],
        ),
        declarationFile,
      );
      await atomicWrite(declarationFile, declarationSource);
      await atomicWrite(join(output, `web-v3.validators.${group}.mjs`), compiled.source);
    }),
  );
};

/** @param {string} directory */
export const generateWebValidators = async (directory) => {
  const output = resolve(directory);
  const manifestPath = join(output, "contracts-manifest.json");
  const { manifest, provisional } = await provisionalInventory(output);
  await Promise.all(
    obsoleteStandaloneValidatorArtifacts.map((name) => rm(join(output, name), { force: true })),
  );
  await formatTypeScriptArtifacts(output, provisional);
  const catalog = await readJson(join(output, "web-v3.catalog.json"));
  const endpoints = endpointInventory(catalog, provisional);
  const groups = validatorGroups(endpoints);
  const webDirectory = resolve(import.meta.dirname, "..");
  const compiledGroups = await compileGroups(output, groups, webDirectory);
  const embeddedPackages = [
    ...new Set(compiledGroups.flatMap(({ compiled }) => compiled.embeddedPackages)),
  ].sort();
  if (embeddedPackages.length === 0) {
    throw new Error("Split standalone validator modules have no embedded package inventory.");
  }
  const lock = /** @type {import("./tooling-types.mjs").PackageLock} */ (
    await readJson(resolve(webDirectory, "package-lock.json"))
  );
  const noticeSource = await validatorNotice(webDirectory, lock, embeddedPackages);
  const wrapperFile = join(output, "web-v3.validation.ts");
  const wrapperSource = await formatTypeScript(validationWrapper(groups), wrapperFile);
  await writeValidatorGroups(output, groups, compiledGroups);
  await atomicWrite(join(output, "web-v3.validators.NOTICE.txt"), noticeSource);
  await atomicWrite(wrapperFile, wrapperSource);
  const combined = combinedManifest(manifest, provisional, lock, embeddedPackages);
  await atomicWrite(manifestPath, `${JSON.stringify(combined)}\n`);
  await assertProvisionalOutput(output, new Set(combined.files));
};
