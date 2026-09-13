import { readFile, readdir, rename, rm, writeFile } from "node:fs/promises";
import { basename, join, resolve } from "node:path";

import { format, resolveConfig } from "prettier";

import { compileStandaloneValidators } from "./standalone-validators.mjs";

const artifacts = [
  "web-v2.validation.ts",
  "web-v2.validators.d.mts",
  "web-v2.validators.mjs",
  "web-v2.validators.NOTICE.txt",
];
const obsoleteArtifacts = ["web-v2.validators.ts"];
const hostSchema = "web-v2.host-failure.schema.json";
const responsesSchema = "web-v2.responses.schema.json";
const safeName = /^[A-Za-z0-9][A-Za-z0-9._-]*$/u;
export const maximumStandaloneValidatorBytes = 1024 * 1024;

const readJson = async (file) => JSON.parse(await readFile(file, "utf8"));
const validatorName = (endpoint) => `validate_${endpoint.replaceAll(/[^A-Za-z0-9_$]/gu, "_")}`;
const formatTypeScript = async (source, filepath) => {
  const canonicalPath = resolve(
    import.meta.dirname,
    "../src/generated/convergence",
    basename(filepath),
  );
  const configuration = await resolveConfig(canonicalPath, { editorconfig: true });
  if (configuration === null) throw new Error("The Web formatting configuration is unavailable.");
  return format(source, { ...configuration, filepath: canonicalPath });
};

const endpointInventory = (catalog, provisional) => {
  if (!Array.isArray(catalog.endpoints) || catalog.endpoints.length === 0) {
    throw new Error("The Web v2 catalog has no endpoint inventory.");
  }
  const seen = new Set();
  return catalog.endpoints.map((endpoint) => {
    const { id, responseDefinition } = endpoint;
    if (
      typeof id !== "string" ||
      seen.has(id) ||
      typeof responseDefinition !== "string" ||
      !safeName.test(responseDefinition)
    ) {
      throw new Error("The Web v2 endpoint inventory is invalid.");
    }
    const responseSchema = `web-v2.endpoint.${id}.response.schema.json`;
    if (
      !safeName.test(responseSchema) ||
      basename(responseSchema) !== responseSchema ||
      !provisional.has(responseSchema)
    ) {
      throw new Error("The Web v2 response-schema inventory is invalid.");
    }
    seen.add(id);
    return { endpoint: id, exportName: validatorName(id), responseDefinition };
  });
};

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

const validatorInventory = async (directory, endpoints) => {
  const aggregate = await readSchema(directory, responsesSchema);
  const host = await readSchema(directory, hostSchema);
  const endpointValidators = endpoints.map(({ exportName, responseDefinition }) => {
    if (aggregate.$defs?.[responseDefinition] === undefined) {
      throw new Error(`Aggregate responses omit ${responseDefinition}.`);
    }
    return {
      exportName,
      schemaReference: `${aggregate.$id}#/$defs/${responseDefinition}`,
    };
  });
  return {
    schemas: [aggregate, host],
    validators: [
      { exportName: "validate_host_failure", schemaReference: host.$id },
      ...endpointValidators,
    ],
  };
};

const declarations = (endpoints) => `/* Generated from ClaimCore.Contracts schemas. Do not edit. */
import type { HostFailure, WebV2ResponseByEndpoint } from "./web-v2.types";

export type WebV2ValidationError = Readonly<{
  instancePath: string;
  schemaPath: string;
  keyword: string;
}>;

export interface WebV2Validator<T> {
  (value: unknown): value is T;
  readonly errors: ReadonlyArray<WebV2ValidationError> | null | undefined;
}

export const validate_host_failure: WebV2Validator<HostFailure>;
${endpoints
  .map(
    ({ endpoint, exportName }) =>
      `export const ${exportName}: WebV2Validator<WebV2ResponseByEndpoint[${JSON.stringify(endpoint)}]>;`,
  )
  .join("\n")}
`;

const wrapper = (endpoints) => `/* Generated from ClaimCore.Contracts schemas. Do not edit. */
import {
  validate_host_failure,
${endpoints.map(({ exportName }) => `  ${exportName},`).join("\n")}
} from "./web-v2.validators.mjs";
import type { WebV2EndpointId } from "./web-v2.endpoint-catalog";
import type { HostFailure, WebV2Response, WebV2ResponseByEndpoint } from "./web-v2.types";

const responseValidators = {
${endpoints.map(({ endpoint, exportName }) => `  ${JSON.stringify(endpoint)}: ${exportName},`).join("\n")}
} satisfies {
  readonly [K in WebV2EndpointId]: (
    value: unknown,
  ) => value is WebV2ResponseByEndpoint[K];
};

export const isHostFailure = (value: unknown): value is HostFailure =>
  validate_host_failure(value);

export const isWebV2Response = <K extends WebV2EndpointId>(
  endpoint: K,
  value: unknown,
): value is WebV2Response<K> => responseValidators[endpoint](value);
`;

const assertProvisionalOutput = async (directory, provisional) => {
  const entries = await readdir(directory, { withFileTypes: true });
  if (entries.some((entry) => !entry.isFile())) {
    throw new Error("Generated convergence output must contain regular files only.");
  }
  const allowed = new Set([
    ...provisional,
    "convergence-manifest.json",
    ...artifacts,
    ...obsoleteArtifacts,
  ]);
  const extra = entries.map((entry) => entry.name).filter((name) => !allowed.has(name));
  const missing = [...provisional].filter((name) => !entries.some((entry) => entry.name === name));
  if (extra.length > 0 || missing.length > 0) {
    throw new Error("The provisional convergence artifact inventory is incomplete or has extras.");
  }
};

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

const formatTypeScriptArtifacts = async (directory, provisional) => {
  const names = [...provisional]
    .filter(
      (name) =>
        name === "web-v2.endpoint-catalog.ts" || /^web-v2\.types(?:\.[a-z]+)*\.ts$/u.test(name),
    )
    .sort();
  if (names.length < 2) throw new Error("Generated Web TypeScript artifacts are missing.");
  for (const name of names) {
    const file = join(directory, name);
    const source = await readFile(file, "utf8");
    await atomicWrite(file, await formatTypeScript(source, file));
  }
};

const packageVersion = (lock, name) => {
  const version = lock.packages?.[`node_modules/${name}`]?.version;
  if (typeof version !== "string") throw new Error(`The locked ${name} version is missing.`);
  return version;
};

const packageNotice = async (webDirectory, lock, name) => {
  const packageDirectory = join(webDirectory, "node_modules", name);
  const manifest = await readJson(join(packageDirectory, "package.json"));
  const version = packageVersion(lock, name);
  if (
    manifest.name !== name ||
    manifest.version !== version ||
    typeof manifest.license !== "string"
  ) {
    throw new Error(`Embedded package metadata is invalid for ${name}.`);
  }
  const files = await readdir(packageDirectory, { withFileTypes: true });
  const licenses = files
    .filter((entry) => entry.isFile() && /^licen[cs]e(?:\.(?:md|txt))?$/iu.test(entry.name))
    .map((entry) => entry.name)
    .sort();
  if (licenses.length !== 1) throw new Error(`Embedded package ${name} needs one license file.`);
  const license = (await readFile(join(packageDirectory, licenses[0]), "utf8")).trim();
  const repository =
    typeof manifest.repository === "string" ? manifest.repository : manifest.repository?.url;
  if (license.length === 0 || typeof repository !== "string" || repository.length === 0) {
    throw new Error(`Embedded package attribution is incomplete for ${name}.`);
  }
  return `Package: ${name}@${version}\nDeclared license: ${manifest.license}\nUpstream: ${repository}\n\n${license}`;
};

const validatorNotice = async (webDirectory, lock, names) => {
  const notices = await Promise.all(names.map((name) => packageNotice(webDirectory, lock, name)));
  const header =
    "ClaimCore Web v2 standalone-validator third-party notices\n\n" +
    "Generated from the exact locked packages whose code is embedded in web-v2.validators.mjs.";
  const separator = `\n\n${"-".repeat(80)}\n\n`;
  return `${header}\n\n${notices.join(separator)}\n`;
};

const combinedManifest = (manifest, provisional, lock, embeddedPackages) => ({
  ...manifest,
  files: [...provisional, ...artifacts].sort(),
  ajv: {
    version: packageVersion(lock, "ajv"),
    formatsVersion: packageVersion(lock, "ajv-formats"),
    bundler: `rolldown@${packageVersion(lock, "rolldown")}`,
    embeddedPackages: embeddedPackages.map((name) => `${name}@${packageVersion(lock, name)}`),
  },
});

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

export const generateWebValidators = async (directory) => {
  const output = resolve(directory);
  const manifestPath = join(output, "convergence-manifest.json");
  const manifest = await readJson(manifestPath);
  if (!Array.isArray(manifest.files) || !manifest.files.every((file) => safeName.test(file))) {
    throw new Error("The provisional convergence manifest is invalid.");
  }
  const provisional = new Set(manifest.files);
  if (
    provisional.size !== manifest.files.length ||
    artifacts.some((artifact) => provisional.has(artifact))
  ) {
    throw new Error("The F# manifest must contain a unique provisional artifact inventory.");
  }
  if (!provisional.has(hostSchema)) throw new Error("The host-failure schema is missing.");
  if (!provisional.has(responsesSchema))
    throw new Error("The aggregate response schema is missing.");
  await assertProvisionalOutput(output, provisional);
  await Promise.all(obsoleteArtifacts.map((name) => rm(join(output, name), { force: true })));
  await formatTypeScriptArtifacts(output, provisional);
  const catalog = await readJson(join(output, "web-v2.catalog.json"));
  const endpoints = endpointInventory(catalog, provisional);
  const inventory = await validatorInventory(output, endpoints);
  const webDirectory = resolve(import.meta.dirname, "..");
  const compiled = await compileStandaloneValidators(inventory, webDirectory);
  const moduleSource = compiled.source;
  if (Buffer.byteLength(moduleSource) > maximumStandaloneValidatorBytes) {
    throw new Error("Standalone validators exceed the one-megabyte generated-code ceiling.");
  }
  await assertValidatorModule(moduleSource, inventory.validators);
  const lock = await readJson(resolve(webDirectory, "package-lock.json"));
  const noticeSource = await validatorNotice(webDirectory, lock, compiled.embeddedPackages);
  const declarationFile = join(output, "web-v2.validators.d.mts");
  const wrapperFile = join(output, "web-v2.validation.ts");
  const declarationSource = await formatTypeScript(declarations(endpoints), declarationFile);
  const wrapperSource = await formatTypeScript(wrapper(endpoints), wrapperFile);
  await atomicWrite(declarationFile, declarationSource);
  await atomicWrite(join(output, "web-v2.validators.mjs"), moduleSource);
  await atomicWrite(join(output, "web-v2.validators.NOTICE.txt"), noticeSource);
  await atomicWrite(wrapperFile, wrapperSource);
  const combined = combinedManifest(manifest, provisional, lock, compiled.embeddedPackages);
  await atomicWrite(manifestPath, `${JSON.stringify(combined)}\n`);
  await assertProvisionalOutput(output, new Set(combined.files));
};
