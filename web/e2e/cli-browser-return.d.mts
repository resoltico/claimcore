import type { Browser, Page } from "@playwright/test";

type BrowserReturnEvidence = {
    location: "issuer" | "callback" | "callback-upgraded" | "browser-error" | "other";
    redirect: boolean;
    requested: boolean;
    response: "none" | "ok" | "refused" | "other";
    network:
        "none" | "refused" | "reset" | "closed" | "empty" | "timeout" | "blocked" | "tls" | "other";
    provider: "none" | "credentials" | "cookie" | "expired" | "disabled" | "other";
    failure:
        | "timeout"
        | "other"
        | "browser-closed"
        | "page-closed"
        | "page-crashed"
        | "target-closed"
        | "resource";
};
export declare const callbackAddress: (url: URL | undefined) => boolean;
export declare const browserReturn: (
    issuer: URL,
    expectedCallback: URL | undefined,
) => {
    observe: (page: Page) => void;
    callbackOrigin: () => string | undefined;
    report: (
        page: Page | undefined,
        error: unknown,
        connected?: boolean,
    ) => Promise<BrowserReturnEvidence>;
};

export declare const isAuthorizationResponse: (
    source: string,
    status: number,
    callback: URL,
    issuer: URL,
    expectedCallback: URL | undefined,
) => boolean;

export declare const browserCompletion: (
    browser: Pick<Browser, "close"> | undefined,
) => Promise<"closed" | "not-started" | "failed">;
