/* Generated from ClaimCore.Contracts schemas. Do not edit. */
import type { WebV3ResponseByEndpoint } from "./web-v3.types";

export type WebV3ValidationError = Readonly<{
    instancePath: string;
    schemaPath: string;
    keyword: string;
}>;

export interface WebV3Validator<T> {
    (value: unknown): value is T;
    readonly errors: ReadonlyArray<WebV3ValidationError> | null | undefined;
}

export const validate_session: WebV3Validator<WebV3ResponseByEndpoint["session"]>;
export const validate_session_logout: WebV3Validator<WebV3ResponseByEndpoint["session.logout"]>;
export const validate_case_get: WebV3Validator<WebV3ResponseByEndpoint["case.get"]>;
export const validate_case_list: WebV3Validator<WebV3ResponseByEndpoint["case.list"]>;
export const validate_case_history: WebV3Validator<WebV3ResponseByEndpoint["case.history"]>;
export const validate_operation_observe: WebV3Validator<
    WebV3ResponseByEndpoint["operation.observe"]
>;
export const validate_command_prepare: WebV3Validator<WebV3ResponseByEndpoint["command.prepare"]>;
export const validate_command_execute: WebV3Validator<WebV3ResponseByEndpoint["command.execute"]>;
export const validate_authority_register: WebV3Validator<
    WebV3ResponseByEndpoint["authority.register"]
>;
export const validate_authority_setGrant: WebV3Validator<
    WebV3ResponseByEndpoint["authority.setGrant"]
>;
export const validate_authority_setEnabled: WebV3Validator<
    WebV3ResponseByEndpoint["authority.setEnabled"]
>;
export const validate_authority_observe: WebV3Validator<
    WebV3ResponseByEndpoint["authority.observe"]
>;
export const validate_authority_approveCopySigner: WebV3Validator<
    WebV3ResponseByEndpoint["authority.approveCopySigner"]
>;
export const validate_authority_approveCopyDeletion: WebV3Validator<
    WebV3ResponseByEndpoint["authority.approveCopyDeletion"]
>;
export const validate_authority_approveCopyAdoption: WebV3Validator<
    WebV3ResponseByEndpoint["authority.approveCopyAdoption"]
>;
export const validate_authority_approveWriterHandoff: WebV3Validator<
    WebV3ResponseByEndpoint["authority.approveWriterHandoff"]
>;
export const validate_authority_reviewRealDataActivation: WebV3Validator<
    WebV3ResponseByEndpoint["authority.reviewRealDataActivation"]
>;
export const validate_authority_approveRealDataActivation: WebV3Validator<
    WebV3ResponseByEndpoint["authority.approveRealDataActivation"]
>;
export const validate_lifecycle_review: WebV3Validator<WebV3ResponseByEndpoint["lifecycle.review"]>;
export const validate_lifecycle_apply: WebV3Validator<WebV3ResponseByEndpoint["lifecycle.apply"]>;
export const validate_lifecycle_approve: WebV3Validator<
    WebV3ResponseByEndpoint["lifecycle.approve"]
>;
export const validate_tombstone_review: WebV3Validator<WebV3ResponseByEndpoint["tombstone.review"]>;
export const validate_tombstone_approvePrune: WebV3Validator<
    WebV3ResponseByEndpoint["tombstone.approvePrune"]
>;
export const validate_tombstone_approveTerminal: WebV3Validator<
    WebV3ResponseByEndpoint["tombstone.approveTerminal"]
>;
export const validate_tombstone_changeHold: WebV3Validator<
    WebV3ResponseByEndpoint["tombstone.changeHold"]
>;
