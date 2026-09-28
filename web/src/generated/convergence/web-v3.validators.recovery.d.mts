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

export const validate_recovery_list: WebV3Validator<WebV3ResponseByEndpoint["recovery.list"]>;
export const validate_recovery_inspect: WebV3Validator<WebV3ResponseByEndpoint["recovery.inspect"]>;
export const validate_recovery_resolve: WebV3Validator<WebV3ResponseByEndpoint["recovery.resolve"]>;
export const validate_recovery_dismiss: WebV3Validator<WebV3ResponseByEndpoint["recovery.dismiss"]>;
export const validate_recovery_export: WebV3Validator<WebV3ResponseByEndpoint["recovery.export"]>;
export const validate_recovery_importEnvelopePreview: WebV3Validator<
    WebV3ResponseByEndpoint["recovery.importEnvelopePreview"]
>;
export const validate_recovery_importEnvelopeRetain: WebV3Validator<
    WebV3ResponseByEndpoint["recovery.importEnvelopeRetain"]
>;
