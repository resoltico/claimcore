/* Generated from ClaimCore.Contracts schemas. Do not edit. */
import type { WebV3EndpointId } from "./web-v3.endpoint-catalog";
import type { WebV3ResponseByEndpoint } from "./web-v3.types";

type ResponseValidator<K extends WebV3EndpointId> = (
  value: unknown,
) => value is WebV3ResponseByEndpoint[K];
type ValidatorModule = Readonly<Record<string, (value: unknown) => boolean>>;
type ValidatorGroup = "discovery" | "core" | "recovery";

const endpointValidators = {
  definition: "validate_definition",
  session: "validate_session",
  "session.logout": "validate_session_logout",
  "case.get": "validate_case_get",
  "case.list": "validate_case_list",
  "case.history": "validate_case_history",
  "operation.observe": "validate_operation_observe",
  "command.prepare": "validate_command_prepare",
  "command.execute": "validate_command_execute",
  "authority.register": "validate_authority_register",
  "authority.setGrant": "validate_authority_setGrant",
  "authority.setEnabled": "validate_authority_setEnabled",
  "authority.observe": "validate_authority_observe",
  "authority.approveCopySigner": "validate_authority_approveCopySigner",
  "authority.approveCopyDeletion": "validate_authority_approveCopyDeletion",
  "authority.approveCopyAdoption": "validate_authority_approveCopyAdoption",
  "authority.approveWriterHandoff": "validate_authority_approveWriterHandoff",
  "authority.reviewRealDataActivation": "validate_authority_reviewRealDataActivation",
  "authority.approveRealDataActivation": "validate_authority_approveRealDataActivation",
  "lifecycle.review": "validate_lifecycle_review",
  "lifecycle.apply": "validate_lifecycle_apply",
  "lifecycle.approve": "validate_lifecycle_approve",
  "tombstone.review": "validate_tombstone_review",
  "tombstone.approvePrune": "validate_tombstone_approvePrune",
  "tombstone.approveTerminal": "validate_tombstone_approveTerminal",
  "tombstone.changeHold": "validate_tombstone_changeHold",
  "recovery.list": "validate_recovery_list",
  "recovery.inspect": "validate_recovery_inspect",
  "recovery.resolve": "validate_recovery_resolve",
  "recovery.dismiss": "validate_recovery_dismiss",
  "recovery.export": "validate_recovery_export",
  "recovery.importEnvelopePreview": "validate_recovery_importEnvelopePreview",
  "recovery.importEnvelopeRetain": "validate_recovery_importEnvelopeRetain",
} as const satisfies Readonly<Record<WebV3EndpointId, string>>;

const endpointGroups = {
  "authority.approveCopyAdoption": "core",
  "authority.approveCopyDeletion": "core",
  "authority.approveCopySigner": "core",
  "authority.approveRealDataActivation": "core",
  "authority.approveWriterHandoff": "core",
  "authority.observe": "core",
  "authority.register": "core",
  "authority.reviewRealDataActivation": "core",
  "authority.setEnabled": "core",
  "authority.setGrant": "core",
  "case.get": "core",
  "case.history": "core",
  "case.list": "core",
  "command.execute": "core",
  "command.prepare": "core",
  definition: "discovery",
  "lifecycle.apply": "core",
  "lifecycle.approve": "core",
  "lifecycle.review": "core",
  "operation.observe": "core",
  "recovery.dismiss": "recovery",
  "recovery.export": "recovery",
  "recovery.importEnvelopePreview": "recovery",
  "recovery.importEnvelopeRetain": "recovery",
  "recovery.inspect": "recovery",
  "recovery.list": "recovery",
  "recovery.resolve": "recovery",
  session: "core",
  "session.logout": "core",
  "tombstone.approvePrune": "core",
  "tombstone.approveTerminal": "core",
  "tombstone.changeHold": "core",
  "tombstone.review": "core",
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

export const isHostFailure = async (value: unknown, status: number): Promise<boolean> => {
  const validator = (await loadHost())["validate_host_failure"]!;
  return validator(value) && (value as { readonly status: number }).status === status;
};

export const isWebV3Response = async <K extends WebV3EndpointId>(
  endpoint: K,
  value: unknown,
): Promise<boolean> => (await requiredValidator(endpoint))(value);
