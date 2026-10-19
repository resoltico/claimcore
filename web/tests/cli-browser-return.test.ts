// @vitest-environment node
import { EventEmitter } from "node:events";
import type { Page } from "@playwright/test";
import { expect, it } from "vitest";
import {
  browserReturn,
  callbackAddress,
  isAuthorizationResponse,
} from "../e2e/cli-browser-return.mjs";

const issuer = new URL("https://identity.example.test/realms/synthetic");
const callback = "http://127.0.0.1:49152/?code=PRIVATE_CODE&state=PRIVATE_STATE";
const fakePage = (address: string, messages: string[] = []) => {
  const events = new EventEmitter();
  const value = {
    on: events.on.bind(events),
    url: () => address,
    locator: () => ({ allTextContents: () => Promise.resolve(messages) }),
  };
  return { page: value as unknown as Page, events };
};
const request = (address: string, errorText = "PRIVATE_ERROR") => ({
  url: () => address,
  failure: () => ({ errorText }),
});
const response = (address: string, status: number, location?: string) => ({
  url: () => address,
  status: () => status,
  headers: () => ({ location }),
});

it("remembers only the issuer callback origin and emits exactly closed return facts", async () => {
  const observer = browserReturn(issuer, undefined);
  const { page, events } = fakePage(callback);
  observer.observe(page);
  events.emit("response", response(issuer.href, 302, callback));
  events.emit("request", request(callback));
  events.emit("response", response(callback, 200));
  expect(observer.callbackOrigin()).toBe("http://127.0.0.1:49152");
  const facts = await observer.report(page, { name: "TimeoutError", message: "PRIVATE_ERROR" });
  expect(facts).toEqual({
    location: "callback",
    redirect: true,
    requested: true,
    response: "ok",
    network: "none",
    provider: "none",
    failure: "timeout",
  });
  expect(JSON.stringify(facts)).not.toContain("PRIVATE");
});

it("refuses foreign, nonredirect and mismatched callback-origin observations", async () => {
  const observer = browserReturn(issuer, new URL(callback));
  const { page, events } = fakePage("https://identity.example.test.evil/private");
  observer.observe(page);
  for (const observed of [
    response("https://evil.example.test/", 302, callback),
    response(issuer.href, 200, callback),
    response(issuer.href, 302, "http://127.0.0.1:49153/?code=PRIVATE"),
    response(issuer.href, 302, "https://127.0.0.1:49152/?code=PRIVATE"),
  ]) {
    events.emit("response", observed);
  }
  events.emit("requestfailed", request("http://127.0.0.1:49153/", "net::ERR_CONNECTION_RESET"));
  expect(await observer.report(page, new Error("PRIVATE"))).toEqual({
    location: "other",
    redirect: false,
    requested: false,
    response: "none",
    network: "none",
    provider: "none",
    failure: "other",
  });
});

it.each([
  ["http://127.0.0.1:49152/?private=value", "callback"],
  ["https://127.0.0.1:49152/?private=value", "callback-upgraded"],
  ["chrome-error://chromewebdata/", "browser-error"],
  [issuer.href, "issuer"],
  ["http://localhost:49152/", "other"],
  ["http://127.0.0.1:49152/private", "other"],
])("classifies the final address without exporting it: case %#", async (address, location) => {
  const observer = browserReturn(issuer, undefined);
  const { page } = fakePage(address);
  expect((await observer.report(page, new Error("PRIVATE"))).location).toBe(location);
});

it.each([
  ["net::ERR_CONNECTION_REFUSED", "refused"],
  ["net::ERR_CONNECTION_RESET", "reset"],
  ["net::ERR_CONNECTION_CLOSED", "closed"],
  ["net::ERR_EMPTY_RESPONSE", "empty"],
  ["net::ERR_TIMED_OUT", "timeout"],
  ["net::ERR_BLOCKED_BY_CLIENT", "blocked"],
  ["net::ERR_BLOCKED_BY_LOCAL_NETWORK_ACCESS_CHECKS", "blocked"],
  ["net::ERR_BLOCKED_BY_PRIVATE_NETWORK_ACCESS_CHECKS", "blocked"],
  ["net::ERR_CERT_AUTHORITY_INVALID", "tls"],
  ["PRIVATE_RAW_NETWORK", "other"],
])(
  "reduces callback network failures to closed categories: case %#",
  async (errorText, network) => {
    const observer = browserReturn(issuer, undefined);
    const { page, events } = fakePage(callback);
    observer.observe(page);
    events.emit("requestfailed", request(callback, errorText));
    const facts = await observer.report(page, new Error("PRIVATE"));
    expect(facts.network).toBe(network);
    expect(JSON.stringify(facts)).not.toContain("PRIVATE");
  },
);

it.each([
  ["Invalid username or password.", "credentials"],
  ["Cookie not found. Please make sure cookies are enabled in your browser.", "cookie"],
  ["Expired code. Please login again.", "expired"],
  ["Account is disabled, contact your administrator.", "disabled"],
  ["PRIVATE_PROVIDER_TEXT", "other"],
  ["", "none"],
])("reduces dedicated provider errors to closed categories: case %#", async (message, provider) => {
  const observer = browserReturn(issuer, undefined);
  const { page } = fakePage(issuer.href, [message]);
  const facts = await observer.report(page, new Error("PRIVATE"));
  expect(facts.provider).toBe(provider);
  expect(JSON.stringify(facts)).not.toContain("PRIVATE");
});

it("retains response refusals while unknown statuses remain closed", async () => {
  for (const [status, category] of [
    [400, "refused"],
    [500, "refused"],
    [302, "other"],
  ] as const) {
    const observer = browserReturn(issuer, undefined);
    const { page, events } = fakePage(callback);
    observer.observe(page);
    events.emit("response", response(callback, status));
    expect((await observer.report(page, null)).response).toBe(category);
  }
  expect(callbackAddress(new URL(callback))).toBe(true);
  expect(callbackAddress(new URL("https://127.0.0.1/"))).toBe(false);
});

it("bounds optional provider inspection when the browser does not answer", async () => {
  const observer = browserReturn(issuer, undefined);
  const page = {
    url: () => issuer.href,
    locator: () => ({
      allTextContents: () =>
        new Promise<string[]>(() => {
          /* Synthetic browser remains pending to exercise the deadline. */
        }),
    }),
  } as unknown as Page;
  expect((await observer.report(page, new Error("PRIVATE"))).provider).toBe("other");
});

it("reports a failed browser launch without exposing its error", async () => {
  expect(
    await browserReturn(issuer, undefined).report(undefined, {
      name: "TimeoutError",
      message: "PRIVATE_URL_AND_CREDENTIAL",
    }),
  ).toEqual({
    location: "other",
    redirect: false,
    requested: false,
    response: "none",
    network: "none",
    provider: "none",
    failure: "timeout",
  });
});

it("prioritizes issuer origins and excludes a different known callback port", async () => {
  const localIssuer = new URL("https://127.0.0.1:5444/realms/synthetic");
  const observer = browserReturn(localIssuer, new URL(callback));
  for (const [address, location] of [
    ["https://127.0.0.1:5444/", "issuer"],
    ["http://127.0.0.1:49153/", "other"],
    ["https://127.0.0.1:49153/", "other"],
  ] as const) {
    const { page } = fakePage(address);
    expect((await observer.report(page, null)).location).toBe(location);
  }
});

it("requires the original provider code and state before the wrong-state control is delivered", () => {
  const original = new URL(callback);
  expect(isAuthorizationResponse(issuer.href, 302, original, issuer, undefined)).toBe(true);
  expect(isAuthorizationResponse(issuer.href, 303, original, issuer, original)).toBe(true);
  expect(original.searchParams.get("code")).toBe("PRIVATE_CODE");
  expect(original.searchParams.get("state")).toBe("PRIVATE_STATE");
});

it.each([
  "http://127.0.0.1:49152/?error=access_denied&state=PRIVATE_STATE",
  "http://127.0.0.1:49152/?state=PRIVATE_STATE",
  "http://127.0.0.1:49152/?code=&state=PRIVATE_STATE",
  "http://127.0.0.1:49152/?code=PRIVATE_CODE",
  "http://127.0.0.1:49152/?code=PRIVATE_CODE&state=",
  "http://127.0.0.1:49152/?code=PRIVATE_CODE&state=PRIVATE_STATE&error=",
])("refuses non-code provider returns before marking delivery: case %#", (address) => {
  expect(isAuthorizationResponse(issuer.href, 302, new URL(address), issuer, undefined)).toBe(
    false,
  );
});

it("refuses authorization-response evidence from another origin, port or nonredirect status", () => {
  const original = new URL(callback);
  expect(
    isAuthorizationResponse("https://foreign.example.test/", 302, original, issuer, undefined),
  ).toBe(false);
  expect(isAuthorizationResponse(issuer.href, 200, original, issuer, undefined)).toBe(false);
  expect(
    isAuthorizationResponse(issuer.href, 302, original, issuer, new URL("http://127.0.0.1:49153/")),
  ).toBe(false);
});
