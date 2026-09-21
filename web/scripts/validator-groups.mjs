const groupNames = ["discovery", "core", "recovery"];

export const standaloneValidatorArtifacts = [
  "web-v2.validation.ts",
  ...groupNames.flatMap((group) => [
    `web-v2.validators.${group}.d.mts`,
    `web-v2.validators.${group}.mjs`,
  ]),
  "web-v2.validators.NOTICE.txt",
];

export const obsoleteStandaloneValidatorArtifacts = [
  "web-v2.validators.d.mts",
  "web-v2.validators.mjs",
  "web-v2.validators.ts",
];

export const validatorName = (endpoint) =>
  `validate_${endpoint.replaceAll(/[^A-Za-z0-9_$]/gu, "_")}`;

const groupForEndpoint = (endpoint) => {
  if (endpoint === "definition") return "discovery";
  return endpoint.startsWith("recovery.") ? "recovery" : "core";
};

export const validatorGroups = (endpoints) => {
  const groups = Object.fromEntries(groupNames.map((name) => [name, []]));
  for (const endpoint of endpoints) groups[groupForEndpoint(endpoint.endpoint)].push(endpoint);
  for (const group of groupNames) {
    if (groups[group].length === 0) throw new Error(`The ${group} validator group is empty.`);
  }
  return groups;
};

const declarations = (endpoints) =>
  endpoints
    .map(
      ({ endpoint, exportName }) =>
        `export const ${exportName}: WebV2Validator<WebV2ResponseByEndpoint[${JSON.stringify(endpoint)}]>;`,
    )
    .join("\n");

export const validatorDeclarations = (
  endpoints,
) => `/* Generated from ClaimCore.Contracts schemas. Do not edit. */
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
${declarations(endpoints)}
`;

const endpointValidators = (endpoints) =>
  endpoints
    .map(
      ({ endpoint, exportName }) => `  ${JSON.stringify(endpoint)}: ${JSON.stringify(exportName)},`,
    )
    .join("\n");

const endpointGroups = (groups) =>
  Object.entries(groups)
    .flatMap(([group, endpoints]) => endpoints.map(({ endpoint }) => [endpoint, group]))
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([endpoint, group]) => `  ${JSON.stringify(endpoint)}: ${JSON.stringify(group)},`)
    .join("\n");

export const validationWrapper = (
  groups,
) => `/* Generated from ClaimCore.Contracts schemas. Do not edit. */
import type { WebV2EndpointId } from "./web-v2.endpoint-catalog";
import type { WebV2ResponseByEndpoint } from "./web-v2.types";

type ResponseValidator<K extends WebV2EndpointId> =
  (value: unknown) => value is WebV2ResponseByEndpoint[K];
type ValidatorModule = Readonly<Record<string, (value: unknown) => boolean>>;
type ValidatorGroup = "discovery" | "core" | "recovery";

const endpointValidators = {
${endpointValidators(Object.values(groups).flat())}
} as const satisfies Readonly<Record<WebV2EndpointId, string>>;

const endpointGroups = {
${endpointGroups(groups)}
} as const satisfies Readonly<Record<WebV2EndpointId, ValidatorGroup>>;

const loadDiscovery = (): Promise<ValidatorModule> => import("./web-v2.validators.discovery.mjs");
const loadCore = (): Promise<ValidatorModule> => import("./web-v2.validators.core.mjs");
const loadRecovery = (): Promise<ValidatorModule> => import("./web-v2.validators.recovery.mjs");

const loaders = { discovery: loadDiscovery, core: loadCore, recovery: loadRecovery };
const validatorsFor = (endpoint: WebV2EndpointId): Promise<ValidatorModule> =>
  loaders[endpointGroups[endpoint]]();

const requiredValidator = async <K extends WebV2EndpointId>(
  endpoint: K,
): Promise<ResponseValidator<K>> =>
  (await validatorsFor(endpoint))[endpointValidators[endpoint]] as ResponseValidator<K>;

export const isHostFailure = async (
  endpoint: WebV2EndpointId,
  value: unknown,
): Promise<boolean> => {
  const validator = (await validatorsFor(endpoint))["validate_host_failure"]!;
  return validator(value);
};

export const isWebV2Response = async <K extends WebV2EndpointId>(
  endpoint: K,
  value: unknown,
): Promise<boolean> => (await requiredValidator(endpoint))(value);
`;
