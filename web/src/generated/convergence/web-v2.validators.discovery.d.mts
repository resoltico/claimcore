/* Generated from ClaimCore.Contracts schemas. Do not edit. */
import type { WebV2ResponseByEndpoint } from "./web-v2.types";

export type WebV2ValidationError = Readonly<{
    instancePath: string;
    schemaPath: string;
    keyword: string;
}>;

export interface WebV2Validator<T> {
    (value: unknown): value is T;
    readonly errors: ReadonlyArray<WebV2ValidationError> | null | undefined;
}

export const validate_definition: WebV2Validator<WebV2ResponseByEndpoint["definition"]>;
