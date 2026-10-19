import type { Page } from "@playwright/test";

type ProviderFailure = "none" | "credentials" | "cookie" | "expired" | "disabled" | "other";
type SubmissionEvidence = Readonly<{
    status: number | "none" | "pending" | "failed" | "other";
    credentialMatch: boolean;
}>;
export declare const providerFailure: (page: Page | undefined) => Promise<ProviderFailure>;
export declare const observeOidcSubmission: (
    page: Page,
    origin: string,
    credentials: Readonly<{ username: string; password: string }>,
) => () => SubmissionEvidence;
