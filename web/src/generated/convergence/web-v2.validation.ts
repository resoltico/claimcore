/* Generated from ClaimCore.Contracts schemas. Do not edit. */
import type { WebV2EndpointId } from "./web-v2.endpoint-catalog";
import type { WebV2ResponseByEndpoint } from "./web-v2.types";

type ResponseValidator<K extends WebV2EndpointId> = (
  value: unknown,
) => value is WebV2ResponseByEndpoint[K];
type ValidatorModule = Readonly<Record<string, (value: unknown) => boolean>>;
type ValidatorGroup = "core" | "recovery";

const endpointValidators = {
  session: "validate_session",
  "session.login": "validate_session_login",
  "session.logout": "validate_session_logout",
  definition: "validate_definition",
  "case.get": "validate_case_get",
  "case.list": "validate_case_list",
  "case.history": "validate_case_history",
  "operation.observe": "validate_operation_observe",
  "command.prepare": "validate_command_prepare",
  "command.execute": "validate_command_execute",
  "recovery.list": "validate_recovery_list",
  "recovery.inspect": "validate_recovery_inspect",
  "recovery.resolve": "validate_recovery_resolve",
  "recovery.dismiss": "validate_recovery_dismiss",
  "recovery.export": "validate_recovery_export",
  "recovery.importEnvelopePreview": "validate_recovery_importEnvelopePreview",
  "recovery.importEnvelopeRetain": "validate_recovery_importEnvelopeRetain",
  "recovery.importRecordPreview": "validate_recovery_importRecordPreview",
  "recovery.importRecordRetain": "validate_recovery_importRecordRetain",
} as const satisfies Readonly<Record<WebV2EndpointId, string>>;

const endpointGroups = {
  "case.get": "core",
  "case.history": "core",
  "case.list": "core",
  "command.execute": "core",
  "command.prepare": "core",
  definition: "core",
  "operation.observe": "core",
  "recovery.dismiss": "recovery",
  "recovery.export": "recovery",
  "recovery.importEnvelopePreview": "recovery",
  "recovery.importEnvelopeRetain": "recovery",
  "recovery.importRecordPreview": "recovery",
  "recovery.importRecordRetain": "recovery",
  "recovery.inspect": "recovery",
  "recovery.list": "recovery",
  "recovery.resolve": "recovery",
  session: "core",
  "session.login": "core",
  "session.logout": "core",
} as const satisfies Readonly<Record<WebV2EndpointId, ValidatorGroup>>;

const loadCore = (): Promise<ValidatorModule> => import("./web-v2.validators.core.mjs");
const loadRecovery = (): Promise<ValidatorModule> => import("./web-v2.validators.recovery.mjs");

const validatorsFor = (endpoint: WebV2EndpointId): Promise<ValidatorModule> =>
  endpointGroups[endpoint] === "recovery" ? loadRecovery() : loadCore();

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
