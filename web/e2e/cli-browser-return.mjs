import { URL } from "node:url";
import { providerFailure } from "./oidc-provider-evidence.mjs";

const parsed = (value, base) => {
  try {
    return new URL(value, base);
  } catch {
    return undefined;
  }
};
export const callbackAddress = (url) =>
  url?.protocol === "http:" && url.hostname === "127.0.0.1" && url.pathname === "/";
export const isAuthorizationResponse = (source, status, callback, issuer, expectedCallback) =>
  parsed(source)?.origin === issuer.origin &&
  [302, 303, 307, 308].includes(status) &&
  callbackAddress(callback) &&
  (expectedCallback === undefined || callback.origin === expectedCallback.origin) &&
  Boolean(callback.searchParams.get("code")) &&
  Boolean(callback.searchParams.get("state")) &&
  !callback.searchParams.has("error");
const callbackTarget = (url) =>
  ["http:", "https:"].includes(url?.protocol) &&
  url.hostname === "127.0.0.1" &&
  url.pathname === "/";
const locationClass = (value, issuer, target) => {
  const url = parsed(value);
  if (url?.protocol === "chrome-error:") {
    return "browser-error";
  }
  if (url?.origin === issuer.origin) {
    return "issuer";
  }
  if (target(value)) {
    return callbackAddress(url) ? "callback" : "callback-upgraded";
  }
  return "other";
};
const networks = new Map([
  ["net::ERR_CONNECTION_REFUSED", "refused"],
  ["net::ERR_CONNECTION_RESET", "reset"],
  ["net::ERR_CONNECTION_CLOSED", "closed"],
  ["net::ERR_EMPTY_RESPONSE", "empty"],
  ["net::ERR_TIMED_OUT", "timeout"],
  ["net::ERR_CONNECTION_TIMED_OUT", "timeout"],
  ["net::ERR_BLOCKED_BY_CLIENT", "blocked"],
  ["net::ERR_BLOCKED_BY_RESPONSE", "blocked"],
  ["net::ERR_BLOCKED_BY_LOCAL_NETWORK_ACCESS_CHECKS", "blocked"],
  ["net::ERR_BLOCKED_BY_PRIVATE_NETWORK_ACCESS_CHECKS", "blocked"],
  ["net::ERR_CERT_AUTHORITY_INVALID", "tls"],
  ["net::ERR_CERT_DATE_INVALID", "tls"],
  ["net::ERR_CERT_COMMON_NAME_INVALID", "tls"],
  ["net::ERR_SSL_PROTOCOL_ERROR", "tls"],
]);

const responseFacts = (value, issuer, state, target) => {
  const url = parsed(value.url());
  if (target(value.url())) {
    const status = value.status();
    state.response = status >= 200 && status < 300 ? "ok" : "other";
    if (status >= 400 && status < 600) {
      state.response = "refused";
    }
  }
  const callback = parsed(value.headers().location, value.url());
  if (
    url?.origin === issuer.origin &&
    [302, 303, 307, 308].includes(value.status()) &&
    callbackAddress(callback) &&
    (state.origin === undefined || callback.origin === state.origin)
  ) {
    state.origin = callback.origin;
    state.redirect = true;
  }
};
const issuerNavigation = (request, page, issuer) =>
  parsed(request.url())?.origin === issuer.origin &&
  request.isNavigationRequest?.() === true &&
  request.frame() === page.mainFrame();
const networkFromError = (error) => {
  const message = typeof error?.message === "string" ? error.message : "";
  const code = message.match(/\bnet::ERR_[A-Z0-9_]+\b/u)?.[0];
  return code === undefined ? "none" : (networks.get(code) ?? "other");
};
const failureClass = (error, connected, pageClosed, crashed) => {
  const message = typeof error?.message === "string" ? error.message : "";
  const resource = [
    "pthread_create",
    "Resource temporarily unavailable",
    "Failed to create thread",
  ];
  if (resource.some((marker) => message.includes(marker))) {
    return "resource";
  }
  if (connected === false) {
    return "browser-closed";
  }
  if (crashed) {
    return "page-crashed";
  }
  if (pageClosed) {
    return "page-closed";
  }
  if (error?.constructor?.name === "TargetClosedError") {
    return "target-closed";
  }
  return error?.name === "TimeoutError" ? "timeout" : "other";
};
const observePage = (page, issuer, state, target) => {
  page.on("crash", () => {
    state.crashed = true;
  });
  page.on("request", (request) => {
    if (target(request.url())) {
      state.requested = true;
    }
  });
  page.on("response", (value) => responseFacts(value, issuer, state, target));
  page.on("requestfailed", (request) => {
    if (target(request.url()) || issuerNavigation(request, page, issuer)) {
      state.network = networks.get(request.failure()?.errorText) ?? "other";
    }
  });
};
export const browserReturn = (issuer, expectedCallback) => {
  const state = {
    origin: expectedCallback?.origin,
    redirect: false,
    requested: false,
    response: "none",
    network: "none",
    crashed: false,
  };
  const target = (value) => {
    const url = parsed(value);
    return (
      callbackTarget(url) && (state.origin === undefined || url.host === parsed(state.origin)?.host)
    );
  };
  return {
    observe: (page) => observePage(page, issuer, state, target),
    callbackOrigin: () => state.origin,
    report: async (page, error, connected) => {
      const failure = failureClass(error, connected, page?.isClosed() === true, state.crashed);
      const snapshot = {
        location: locationClass(page?.url(), issuer, target),
        redirect: state.redirect,
        requested: state.requested,
        response: state.response,
        network: state.network === "none" ? networkFromError(error) : state.network,
      };
      const provider = snapshot.location === "issuer" ? await providerFailure(page) : "none";
      return { ...snapshot, provider, failure };
    },
  };
};

export const browserCompletion = async (browser) => {
  if (browser === undefined) {
    return "not-started";
  }
  try {
    await browser.close();
    return "closed";
  } catch {
    return "failed";
  }
};
