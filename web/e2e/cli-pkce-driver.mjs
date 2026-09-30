import { readFileSync, writeFileSync } from "node:fs";
import http from "node:http";
import process from "node:process";
import { URL, URLSearchParams } from "node:url";
import { chromium } from "playwright";

let authorization;
let redirect;
try {
  authorization = new URL(process.argv[2] ?? "");
  redirect = new URL(authorization.searchParams.get("redirect_uri") ?? "");
} catch {
  process.exit(2);
}
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
  redirect.protocol !== "http:" ||
  redirect.hostname !== "127.0.0.1" ||
  redirect.pathname !== "/" ||
  !authorization.searchParams.has("code_challenge") ||
  authorization.searchParams.get("code_challenge_method") !== "S256" ||
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

const requestWrongState = () => {
  redirect.search = new URLSearchParams({ code: "synthetic", state: "wrong-state" }).toString();
  return new Promise((resolve, reject) => {
    const request = http.get(redirect, (response) => {
      response.resume();
      response.on("end", resolve);
    });
    request.on("error", reject);
  });
};

const run = async () => {
  if (mode === "wrong-state") {
    progress("wrong-state-request");
    await requestWrongState();
    return;
  }

  // This browser is confined to the disposable synthetic Keycloak fixture. The CLI itself uses
  // certificate-validated HTTPS with the fixture's explicit private CA, never this browser option.
  const browser = await chromium.launch({ headless: true });
  progress("browser-launched");
  try {
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    const page = await context.newPage();
    await page.goto(authorization.toString());
    progress("authorization-opened");
    if ((await page.locator('input[name="username"]').count()) !== 1) {
      const text = (await page.locator("body").innerText()).toLowerCase();
      if (text.includes("redirect_uri")) {
        progress("redirect-refused");
      } else if (text.includes("client")) {
        progress("client-refused");
      } else {
        progress("login-form-missing");
      }
      throw new Error("Synthetic login form unavailable.");
    }
    await page.locator('input[name="username"]').fill(credentials.users[0].username);
    progress("username-filled");
    await page.locator('input[name="password"]').fill(credentials.users[0].password);
    progress("password-filled");
    const submit = page.locator('button[type="submit"], input[type="submit"]').first();
    if ((await submit.count()) !== 1) {
      progress("submit-control-missing");
      throw new Error("Synthetic submit control unavailable.");
    }
    await submit.click();
    progress("login-submitted");
    await page.waitForURL((url) => url.origin === redirect.origin && url.pathname === "/");
    progress("callback-observed");
    await context.close();
  } finally {
    await browser.close();
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
