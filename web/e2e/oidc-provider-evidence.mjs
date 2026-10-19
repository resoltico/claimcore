import { URL, URLSearchParams } from "node:url";
import { clearTimeout, setTimeout } from "node:timers";

const providerMessages = new Map([
  ["invalid username or password.", "credentials"],
  ["cookie not found. please make sure cookies are enabled in your browser.", "cookie"],
  ["action expired. please continue with login now.", "expired"],
  ["expired code. please login again.", "expired"],
  ["your login attempt timed out. login will start from the beginning.", "expired"],
  ["account is disabled, contact your administrator.", "disabled"],
]);
export const providerFailure = async (page) => {
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

const loginRequest = (request, page, origin) => {
  try {
    const url = new URL(request.url());
    return (
      request.method() === "POST" &&
      request.isNavigationRequest() &&
      request.frame() === page.mainFrame() &&
      url.origin === origin &&
      url.pathname.includes("/login-actions/")
    );
  } catch {
    return false;
  }
};
const matchingCredentials = (request, credentials) => {
  const values = new URLSearchParams(request.postData() ?? "");
  return (
    values.getAll("username").length === 1 &&
    values.getAll("password").length === 1 &&
    values.get("username") === credentials.username &&
    values.get("password") === credentials.password
  );
};
export const observeOidcSubmission = (page, origin, credentials) => {
  let submitted;
  let status = "none";
  let credentialMatch = false;
  page.on("request", (request) => {
    if (submitted === undefined && loginRequest(request, page, origin)) {
      submitted = request;
      status = "pending";
      credentialMatch = matchingCredentials(request, credentials);
    }
  });
  page.on("response", (response) => {
    if (submitted !== undefined && response.request() === submitted) {
      const code = response.status();
      status = Number.isInteger(code) && code >= 100 && code <= 599 ? code : "other";
    }
  });
  page.on("requestfailed", (request) => {
    if (request === submitted) {
      status = "failed";
    }
  });
  return () => ({ status, credentialMatch });
};
