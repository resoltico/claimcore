import { readFileSync, writeFileSync } from "node:fs";
import http from "node:http";
import process from "node:process";
import { URL, URLSearchParams } from "node:url";
import { chromium } from "@playwright/test";
import { browserReturn, callbackAddress, isAuthorizationResponse } from "./cli-browser-return.mjs";

let authorization;
let redirect;
try {
  authorization = new URL(process.argv[2] ?? "");
  const supplied = authorization.searchParams.get("redirect_uri");
  redirect = supplied === null ? undefined : new URL(supplied);
} catch {
  process.exit(2);
}
const pushed = authorization.searchParams.has("request_uri");
const mode = process.env.CLAIMCORE_CLI_TEST_AUTH_MODE;
const credentialsFile = process.env.CLAIMCORE_CLI_TEST_CREDENTIALS_FILE;
const issuerText = process.env.CLAIMCORE_CLI_TEST_ISSUER;
const publicClientId = process.env.CLAIMCORE_CLI_TEST_PUBLIC_CLIENT_ID;
const progressFile = process.env.CLAIMCORE_CLI_TEST_PROGRESS_FILE;
let currentStage = "start";
const progress = (stage) => {
  currentStage = stage;
  if (progressFile) {
    writeFileSync(progressFile, `${stage}\n`, { mode: 0o600 });
  }
};
progress("driver-started");

let issuer;
try {
  issuer = new URL(issuerText ?? "");
} catch {
  process.exit(2);
}

if (
  authorization.protocol !== "https:" ||
  authorization.origin !== issuer.origin ||
  authorization.pathname !== `${issuer.pathname}/protocol/openid-connect/auth` ||
  authorization.searchParams.get("client_id") !== publicClientId ||
  (!pushed &&
    (redirect === undefined ||
      !callbackAddress(redirect) ||
      !authorization.searchParams.has("code_challenge") ||
      authorization.searchParams.get("code_challenge_method") !== "S256")) ||
  !credentialsFile ||
  (mode !== "valid" && mode !== "wrong-state")
) {
  process.exit(2);
}

let credentials;
try {
  credentials = JSON.parse(readFileSync(credentialsFile, "utf8"));
} catch {
  process.exit(2);
}
if (!Array.isArray(credentials.users) || credentials.users.length < 1) {
  process.exit(2);
}

const selectedUsername = process.env.CLAIMCORE_CLI_TEST_USERNAME;
const selectedUser =
  selectedUsername === undefined
    ? credentials.users[0]
    : credentials.users.find((user) => user?.username === selectedUsername);
if (
  selectedUser === undefined ||
  typeof selectedUser.username !== "string" ||
  typeof selectedUser.password !== "string"
) {
  process.exit(2);
}

const deliverCallback = (address) =>
  new Promise((resolve, reject) => {
    const request = http.get(address, (response) => {
      response.resume();
      response.on("end", resolve);
    });
    request.on("error", reject);
  });

const requestWrongState = () => {
  if (redirect === undefined) {
    return Promise.reject(new Error("Callback address unavailable."));
  }
  redirect.search = new URLSearchParams({ code: "synthetic", state: "wrong-state" }).toString();
  return deliverCallback(redirect);
};

const login = async (page) => {
  await page.goto(authorization.toString());
  progress("authorization-opened");
  if ((await page.locator('input[name="username"]').count()) !== 1) {
    progress("login-form-missing");
    throw new Error("Synthetic login form unavailable.");
  }
  await page.locator('input[name="username"]').fill(selectedUser.username);
  progress("username-filled");
  await page.locator('input[name="password"]').fill(selectedUser.password);
  progress("password-filled");
  const submit = page.locator('button[type="submit"], input[type="submit"]').first();
  if ((await submit.count()) !== 1) {
    throw new Error("Synthetic submit control unavailable.");
  }
  await submit.click();
  progress("login-submitted");
};

const rejectCallbackState = async (context) => {
  let delivered = false;
  if (mode !== "wrong-state") {
    return () => delivered;
  }
  await context.route("**/*", async (route) => {
    const response = await route.fetch({ maxRedirects: 0 });
    const { location } = response.headers();
    if (location !== undefined) {
      const callback = new URL(location, route.request().url());
      if (callbackAddress(callback)) {
        if (
          !isAuthorizationResponse(response.url(), response.status(), callback, issuer, redirect)
        ) {
          throw new Error("Synthetic provider did not issue an authorization code response.");
        }
        redirect = new URL(callback.origin);
        callback.searchParams.set("state", "wrong-state");
        await deliverCallback(callback);
        delivered = true;
        await route.fulfill({
          status: 200,
          contentType: "text/plain",
          body: "Synthetic callback delivered.",
        });
        return;
      }
    }
    await route.fulfill({ response });
  });
  return () => delivered;
};

const retainReturn = async (observation, page, error) => {
  try {
    if (progressFile) {
      writeFileSync(
        `${progressFile}.return`,
        JSON.stringify(await observation.report(page, error)),
        {
          mode: 0o600,
        },
      );
    }
  } catch {
    // Evidence storage must not replace the original synthetic browser failure.
  }
};

const run = async () => {
  // Only the disposable synthetic browser ignores its fixture certificate. CLI HTTPS validates
  // its explicit CA. Inspect the provider redirect because routes do not intercept redirect hops.
  const observation = browserReturn(issuer, redirect);
  let browser;
  let page;
  try {
    browser = await chromium.launch({ headless: true });
    progress("browser-launched");
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    const wrongStateDelivered = await rejectCallbackState(context);
    page = await context.newPage();
    observation.observe(page);
    await login(page);
    if (mode === "wrong-state") {
      if (!wrongStateDelivered()) {
        throw new Error("Synthetic state control was not delivered.");
      }
      progress("wrong-state-delivered");
    } else {
      await page.waitForURL((url) => callbackAddress(url));
      progress("callback-observed");
    }
    await context.close();
  } catch (error) {
    const remembered = observation.callbackOrigin();
    if (redirect === undefined && remembered !== undefined) {
      redirect = new URL(remembered);
    }
    await retainReturn(observation, page, error);
    throw error;
  } finally {
    await browser?.close();
  }
};

try {
  await run();
} catch {
  if (progressFile) {
    writeFileSync(progressFile, `driver-refused-after-${currentStage}\n`, { mode: 0o600 });
  }
  try {
    await requestWrongState();
  } catch {
    // The CLI may already have closed its listener; keep the original safe stage category.
  }
  // Never write the authorization URL, callback code, credential, or provider content to logs.
  process.exitCode = 1;
}
