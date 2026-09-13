/* Generated from ClaimCore.Contracts schemas. Do not edit. */
import {
  validate_host_failure,
  validate_session,
  validate_session_login,
  validate_session_logout,
  validate_definition,
  validate_case_get,
  validate_case_list,
  validate_case_history,
  validate_operation_observe,
  validate_command_prepare,
  validate_command_execute,
  validate_recovery_list,
  validate_recovery_inspect,
  validate_recovery_resolve,
  validate_recovery_dismiss,
  validate_recovery_export,
  validate_recovery_importEnvelopePreview,
  validate_recovery_importEnvelopeRetain,
  validate_recovery_importRecordPreview,
  validate_recovery_importRecordRetain,
} from "./web-v2.validators.mjs";
import type { WebV2EndpointId } from "./web-v2.endpoint-catalog";
import type { HostFailure, WebV2Response, WebV2ResponseByEndpoint } from "./web-v2.types";

const responseValidators = {
  session: validate_session,
  "session.login": validate_session_login,
  "session.logout": validate_session_logout,
  definition: validate_definition,
  "case.get": validate_case_get,
  "case.list": validate_case_list,
  "case.history": validate_case_history,
  "operation.observe": validate_operation_observe,
  "command.prepare": validate_command_prepare,
  "command.execute": validate_command_execute,
  "recovery.list": validate_recovery_list,
  "recovery.inspect": validate_recovery_inspect,
  "recovery.resolve": validate_recovery_resolve,
  "recovery.dismiss": validate_recovery_dismiss,
  "recovery.export": validate_recovery_export,
  "recovery.importEnvelopePreview": validate_recovery_importEnvelopePreview,
  "recovery.importEnvelopeRetain": validate_recovery_importEnvelopeRetain,
  "recovery.importRecordPreview": validate_recovery_importRecordPreview,
  "recovery.importRecordRetain": validate_recovery_importRecordRetain,
} satisfies {
  readonly [K in WebV2EndpointId]: (value: unknown) => value is WebV2ResponseByEndpoint[K];
};

export const isHostFailure = (value: unknown): value is HostFailure => validate_host_failure(value);

export const isWebV2Response = <K extends WebV2EndpointId>(
  endpoint: K,
  value: unknown,
): value is WebV2Response<K> => responseValidators[endpoint](value);
