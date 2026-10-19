import type { Page } from "@playwright/test";

type BrowserReturnEvidence = {
    location: "issuer" | "callback" | "callback-upgraded" | "browser-error" | "other";
    redirect: boolean;
    requested: boolean;
    response: "none" | "ok" | "refused" | "other";
    network:
        "none" | "refused" | "reset" | "closed" | "empty" | "timeout" | "blocked" | "tls" | "other";
    provider: "none" | "credentials" | "cookie" | "expired" | "disabled" | "other";
    failure: "timeout" | "other";
};
export declare const callbackAddress: (url: URL | undefined) => boolean;
export declare const browserReturn: (
    issuer: URL,
    expectedCallback: URL | undefined,
) => {
    observe: (page: Page) => void;
    callbackOrigin: () => string | undefined;
    report: (page: Page | undefined, error: unknown) => Promise<BrowserReturnEvidence>;
};

export declare const isAuthorizationResponse: (
    source: string,
    status: number,
    callback: URL,
    issuer: URL,
    expectedCallback: URL | undefined,
) => boolean;
