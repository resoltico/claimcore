/* Generated from ClaimCore.Contracts schemas. Do not edit. */
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
export const validate_session: WebV2Validator<WebV2ResponseByEndpoint["session"]>;
export const validate_session_login: WebV2Validator<WebV2ResponseByEndpoint["session.login"]>;
export const validate_session_logout: WebV2Validator<WebV2ResponseByEndpoint["session.logout"]>;
export const validate_definition: WebV2Validator<WebV2ResponseByEndpoint["definition"]>;
export const validate_case_get: WebV2Validator<WebV2ResponseByEndpoint["case.get"]>;
export const validate_case_list: WebV2Validator<WebV2ResponseByEndpoint["case.list"]>;
export const validate_case_history: WebV2Validator<WebV2ResponseByEndpoint["case.history"]>;
export const validate_operation_observe: WebV2Validator<
    WebV2ResponseByEndpoint["operation.observe"]
>;
export const validate_command_prepare: WebV2Validator<WebV2ResponseByEndpoint["command.prepare"]>;
export const validate_command_execute: WebV2Validator<WebV2ResponseByEndpoint["command.execute"]>;
