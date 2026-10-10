const endpointGroupNames = /** @type {const} */ (["discovery", "core", "recovery"]);
const groupNames = ["host", ...endpointGroupNames];

export const standaloneValidatorArtifacts = [
  "web-v3.validation.ts",
  ...groupNames.flatMap((group) => [
    `web-v3.validators.${group}.d.mts`,
    `web-v3.validators.${group}.mjs`,
  ]),
  "web-v3.validators.NOTICE.txt",
];

export const obsoleteStandaloneValidatorArtifacts = [
  "web-v3.validators.d.mts",
  "web-v3.validators.mjs",
  "web-v3.validators.ts",
];

/** @param {string} endpoint */
export const validatorName = (endpoint) =>
  `validate_${endpoint.replaceAll(/[^A-Za-z0-9_$]/gu, "_")}`;

/** @param {string} endpoint */
const groupForEndpoint = (endpoint) => {
  if (endpoint === "definition") {
    return "discovery";
  }
  return endpoint.startsWith("recovery.") ? "recovery" : "core";
};

/** @param {import("./tooling-types.mjs").ValidatorEndpoint[]} endpoints @returns {import("./tooling-types.mjs").ValidatorGroups} */
export const validatorGroups = (endpoints) => {
  /** @type {import("./tooling-types.mjs").ValidatorGroups} */
  const groups = { host: [], discovery: [], core: [], recovery: [] };
  for (const endpoint of endpoints) {
    groups[groupForEndpoint(endpoint.endpoint)].push(endpoint);
  }
  for (const group of endpointGroupNames) {
    if (groups[group].length === 0) {
      throw new Error(`The ${group} validator group is empty.`);
    }
  }
  return groups;
};

/** @param {import("./tooling-types.mjs").ValidatorEndpoint[]} endpoints */
const declarations = (endpoints) =>
  endpoints
    .map(
      ({ endpoint, exportName }) =>
        `export const ${exportName}: WebV3Validator<WebV3ResponseByEndpoint[${JSON.stringify(endpoint)}]>;`,
    )
    .join("\n");

/** @param {import("./tooling-types.mjs").ValidatorEndpoint[]} endpoints */
export const validatorDeclarations = (
  endpoints,
) => `/* Generated from ClaimCore.Contracts schemas. Do not edit. */
${endpoints.length === 0 ? 'import type { HostFailure } from "./web-v3.types";' : 'import type { WebV3ResponseByEndpoint } from "./web-v3.types";'}

export type WebV3ValidationError = Readonly<{
  instancePath: string;
  schemaPath: string;
  keyword: string;
}>;

export interface WebV3Validator<T> {
  (value: unknown): value is T;
  readonly errors: ReadonlyArray<WebV3ValidationError> | null | undefined;
}

${endpoints.length === 0 ? "export const validate_host_failure: WebV3Validator<HostFailure>;" : ""}
${declarations(endpoints)}
${endpoints.some(({ endpoint }) => endpoint.startsWith("recovery.")) ? "export const validate_recovery_artifact: WebV3Validator<unknown>;" : ""}
`;

/** @param {import("./tooling-types.mjs").ValidatorEndpoint[]} endpoints */
const endpointValidators = (endpoints) =>
  endpoints
    .map(
      ({ endpoint, exportName }) => `  ${JSON.stringify(endpoint)}: ${JSON.stringify(exportName)},`,
    )
    .join("\n");

/** @param {import("./tooling-types.mjs").ValidatorGroups} groups */
const endpointGroups = (groups) =>
  Object.entries(groups)
    .flatMap(([group, endpoints]) =>
      endpoints.map(({ endpoint }) => /** @type {[string, string]} */ ([endpoint, group])),
    )
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([endpoint, group]) => `  ${JSON.stringify(endpoint)}: ${JSON.stringify(group)},`)
    .join("\n");

/** @param {import("./tooling-types.mjs").ValidatorGroups} groups */
export const validationWrapper = (
  groups,
) => `/* Generated from ClaimCore.Contracts schemas. Do not edit. */
import type { WebV3EndpointId } from "./web-v3.endpoint-catalog";
import type { WebV3ResponseByEndpoint } from "./web-v3.types";

type ResponseValidator<K extends WebV3EndpointId> =
  (value: unknown) => value is WebV3ResponseByEndpoint[K];
type ValidatorModule = Readonly<Record<string, (value: unknown) => boolean>>;
type ValidatorGroup = "discovery" | "core" | "recovery";

const endpointValidators = {
${endpointValidators(Object.values(groups).flat())}
} as const satisfies Readonly<Record<WebV3EndpointId, string>>;

const endpointGroups = {
${endpointGroups(groups)}
} as const satisfies Readonly<Record<WebV3EndpointId, ValidatorGroup>>;

const loadHost = (): Promise<ValidatorModule> => import("./web-v3.validators.host.mjs");
const loadDiscovery = (): Promise<ValidatorModule> => import("./web-v3.validators.discovery.mjs");
const loadCore = (): Promise<ValidatorModule> => import("./web-v3.validators.core.mjs");
const loadRecovery = (): Promise<ValidatorModule> => import("./web-v3.validators.recovery.mjs");

const loaders = { discovery: loadDiscovery, core: loadCore, recovery: loadRecovery };
const validatorsFor = (endpoint: WebV3EndpointId): Promise<ValidatorModule> =>
  loaders[endpointGroups[endpoint]]();

const requiredValidator = async <K extends WebV3EndpointId>(
  endpoint: K,
): Promise<ResponseValidator<K>> =>
  (await validatorsFor(endpoint))[endpointValidators[endpoint]] as ResponseValidator<K>;

export const isHostFailure = async (
  value: unknown,
  status: number,
): Promise<boolean> => {
  const validator = (await loadHost())["validate_host_failure"]!;
  return validator(value) && (value as { readonly status: number }).status === status;
};

export const isRecoveryArtifact = async (value: unknown, operationId: string): Promise<boolean> =>
  (await loadRecovery())["validate_recovery_artifact"]!(value) &&
  (value as { operationId: string }).operationId === operationId;

export const isWebV3Response = async <K extends WebV3EndpointId>(
  endpoint: K,
  value: unknown,
): Promise<boolean> => (await requiredValidator(endpoint))(value);
`;
