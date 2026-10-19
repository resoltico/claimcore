import { URL } from "node:url";
import { clearTimeout, setTimeout } from "node:timers";

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
const providerMessages = new Map([
  ["invalid username or password.", "credentials"],
  ["cookie not found. please make sure cookies are enabled in your browser.", "cookie"],
  ["action expired. please continue with login now.", "expired"],
  ["expired code. please login again.", "expired"],
  ["your login attempt timed out. login will start from the beginning.", "expired"],
  ["account is disabled, contact your administrator.", "disabled"],
]);
const providerClass = async (page) => {
  if (page === undefined) {
    return "none";
  }
  let timer;
  try {
    const messages = await Promise.race([
      page.locator("#input-error, .kc-feedback-text, #kc-error-message p").allTextContents(),
      new Promise((resolve) => {
        timer = setTimeout(() => resolve(undefined), 1000);
      }),
    ]);
    if (messages === undefined) {
      return "other";
    }
    const present = messages.map((text) => text.trim().replaceAll(/\s+/gu, " ").toLowerCase());
    const message = present.find((text) => text.length > 0);
    return message === undefined ? "none" : (providerMessages.get(message) ?? "other");
  } catch {
    return "other";
  } finally {
    clearTimeout(timer);
  }
};

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
export const browserReturn = (issuer, expectedCallback) => {
  const state = {
    origin: expectedCallback?.origin,
    redirect: false,
    requested: false,
    response: "none",
    network: "none",
  };
  const target = (value) => {
    const url = parsed(value);
    return (
      callbackTarget(url) && (state.origin === undefined || url.host === parsed(state.origin)?.host)
    );
  };
  return {
    observe: (page) => {
      page.on("request", (request) => {
        if (target(request.url())) {
          state.requested = true;
        }
      });
      page.on("response", (value) => responseFacts(value, issuer, state, target));
      page.on("requestfailed", (request) => {
        if (target(request.url())) {
          state.network = networks.get(request.failure()?.errorText) ?? "other";
        }
      });
    },
    callbackOrigin: () => state.origin,
    report: async (page, error) => ({
      location: locationClass(page?.url(), issuer, target),
      redirect: state.redirect,
      requested: state.requested,
      response: state.response,
      network: state.network,
      provider:
        locationClass(page?.url(), issuer, target) === "issuer"
          ? await providerClass(page)
          : "none",
      failure: error?.name === "TimeoutError" ? "timeout" : "other",
    }),
  };
};
