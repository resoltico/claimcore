/* Generated from ClaimCore.Contracts schemas. Do not edit. */
import type { HostFailure } from "./web-v3.types";

export type WebV3ValidationError = Readonly<{
    instancePath: string;
    schemaPath: string;
    keyword: string;
}>;

export interface WebV3Validator<T> {
    (value: unknown): value is T;
    readonly errors: ReadonlyArray<WebV3ValidationError> | null | undefined;
}

export const validate_host_failure: WebV3Validator<HostFailure>;
