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
export const validate_recovery_list: WebV2Validator<WebV2ResponseByEndpoint["recovery.list"]>;
export const validate_recovery_inspect: WebV2Validator<WebV2ResponseByEndpoint["recovery.inspect"]>;
export const validate_recovery_resolve: WebV2Validator<WebV2ResponseByEndpoint["recovery.resolve"]>;
export const validate_recovery_dismiss: WebV2Validator<WebV2ResponseByEndpoint["recovery.dismiss"]>;
export const validate_recovery_export: WebV2Validator<WebV2ResponseByEndpoint["recovery.export"]>;
export const validate_recovery_importEnvelopePreview: WebV2Validator<
    WebV2ResponseByEndpoint["recovery.importEnvelopePreview"]
>;
export const validate_recovery_importEnvelopeRetain: WebV2Validator<
    WebV2ResponseByEndpoint["recovery.importEnvelopeRetain"]
>;
export const validate_recovery_importRecordPreview: WebV2Validator<
    WebV2ResponseByEndpoint["recovery.importRecordPreview"]
>;
export const validate_recovery_importRecordRetain: WebV2Validator<
    WebV2ResponseByEndpoint["recovery.importRecordRetain"]
>;
