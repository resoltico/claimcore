const endpointGroupNames = /** @type {const} */ (["discovery", "core", "authority"]);
const groupNames = ["host", ...endpointGroupNames];

export const standaloneValidatorArtifacts = [
  "validation.ts",
  ...groupNames.flatMap((group) => [`validators.${group}.d.mts`, `validators.${group}.mjs`]),
  "validators.NOTICE.txt",
];

/** @param {string} endpoint */
export const validatorName = (endpoint) =>
  `validate_${endpoint.replaceAll(/[^A-Za-z0-9_$]/gu, "_")}`;

/** @param {string} endpoint */
const groupForEndpoint = (endpoint) => {
  if (endpoint === "definition") {
    return "discovery";
  }
  return endpoint.startsWith("authority.") ? "authority" : "core";
};

/** @param {import("./tooling-types.mjs").ValidatorEndpoint[]} endpoints @returns {import("./tooling-types.mjs").ValidatorGroups} */
export const validatorGroups = (endpoints) => {
  /** @type {import("./tooling-types.mjs").ValidatorGroups} */
  const groups = { host: [], discovery: [], core: [], authority: [] };
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
        `export const ${exportName}: Validator<ResponseByEndpoint[${JSON.stringify(endpoint)}]>;`,
    )
    .join("\n");

/** @param {import("./tooling-types.mjs").ValidatorEndpoint[]} endpoints */
export const validatorDeclarations = (
  endpoints,
) => `/* Generated from ClaimCore.Contracts schemas. Do not edit. */
${endpoints.length === 0 ? 'import type { HostFailure } from "./types";' : 'import type { ResponseByEndpoint } from "./types";'}

export type ValidationError = Readonly<{
  instancePath: string;
  schemaPath: string;
  keyword: string;
}>;

export interface Validator<T> {
  (value: unknown): value is T;
  readonly errors: ReadonlyArray<ValidationError> | null | undefined;
}

${endpoints.length === 0 ? "export const validate_host_failure: Validator<HostFailure>;" : ""}
${declarations(endpoints)}

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
import type { EndpointId } from "./endpoint-catalog";
import type { ResponseByEndpoint } from "./types";

type ResponseValidator<K extends EndpointId> =
  (value: unknown) => value is ResponseByEndpoint[K];
type ValidatorModule = Readonly<Record<string, (value: unknown) => boolean>>;
type ValidatorGroup = "discovery" | "core" | "authority";

const endpointValidators = {
${endpointValidators(Object.values(groups).flat())}
} as const satisfies Readonly<Record<EndpointId, string>>;

const endpointGroups = {
${endpointGroups(groups)}
} as const satisfies Readonly<Record<EndpointId, ValidatorGroup>>;

const loadHost = (): Promise<ValidatorModule> => import("./validators.host.mjs");
const loadDiscovery = (): Promise<ValidatorModule> => import("./validators.discovery.mjs");
const loadCore = (): Promise<ValidatorModule> => import("./validators.core.mjs");
const loadAuthority = (): Promise<ValidatorModule> => import("./validators.authority.mjs");

const loaders = { discovery: loadDiscovery, core: loadCore, authority: loadAuthority };
const validatorsFor = (endpoint: EndpointId): Promise<ValidatorModule> =>
  loaders[endpointGroups[endpoint]]();

const requiredValidator = async <K extends EndpointId>(
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

export const isResponse = async <K extends EndpointId>(
  endpoint: K,
  value: unknown,
): Promise<boolean> => (await requiredValidator(endpoint))(value);
`;
