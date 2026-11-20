import { spawnSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import https from "node:https";
import net from "node:net";
import path from "node:path";
import process from "node:process";
import { chromium, expect } from "@playwright/test";

const [root, owned] = process.argv.slice(2);
const { consumerContractIdentity, presentationLanguages, presentationPseudolanguage } =
  await import(path.join(root, "contracts/client.ts"));
const { operationId } = await import(path.join(root, "web/src/operation/operationId.ts"));
const configuration = path.join(owned, "configuration $CLAIMCORE_QA_PATH # :, \" \\\' end\\");
const composeFile = path.join(root, "deployment/compose.yaml");
const cleanupImage = JSON.parse(
  readFileSync(path.join(root, "db/postgresql-baseline.json"), "utf8"),
).supportedMajors[0].containerImage;
let project;
let stage = "configure";
let sequence = 0;
const progress = (value) => {
  stage = value;
  writeFileSync(path.join(owned, "deployment.progress"), `${stage}\n`, { mode: 0o600 });
};
const command = (executable, args, timeout = 1_200_000, expectedExit = 0) => {
  // Compose interpolation must never select an operator's existing configuration or project.
  const environment = Object.fromEntries(
    Object.entries(process.env).filter(
      ([name]) => !name.startsWith("CLAIMCORE_") && !name.startsWith("COMPOSE_"),
    ),
  );
  const result = spawnSync(executable, args, {
    cwd: root,
    env: environment,
    timeout,
    maxBuffer: 8_388_608,
    encoding: "utf8",
    stdio: "pipe",
  });
  const prefix = process.argv[4] === "cleanup" ? "cleanup-command" : "command";
  const output = [result.stdout ?? "", result.stderr ?? ""].join("\n");
  writeFileSync(path.join(owned, `${prefix}-${sequence++}.log`), output, { mode: 0o600 });
  if (result.error !== undefined || result.status !== expectedExit) {
    throw new Error("Owned deployment command refused.", { cause: result.error });
  }
  return result.stdout;
};
const compose = (...args) => {
  if (!/^claimcore-[a-f0-9]{16}$/u.test(project ?? "")) {
    throw new Error("Invalid owned project identity.");
  }
  return command("docker", [
    "compose",
    "--project-name",
    project,
    "--env-file",
    path.join(configuration, "compose.env"),
    "--file",
    composeFile,
    ...args,
  ]);
};
const rejectUnstartedIdentity = () => {
  const configurationValue = JSON.parse(compose("config", "--format", "json"));
  const { identity } = configurationValue.services;
  const probe = identity.healthcheck.test;
  if (probe[0] !== "CMD") {
    throw new Error("The owned identity readiness probe must be an explicit command.");
  }
  command(
    "docker",
    [
      "run",
      "--rm",
      "--name",
      `${project}-readiness-negative`,
      "--label",
      `claimcore.configuration=${project}`,
      "--network",
      "none",
      "--entrypoint",
      probe[1],
      identity.image,
      ...probe.slice(2),
    ],
    120_000,
    1,
  );
};
const qualifyIdentityMemory = () => {
  const identity = compose("ps", "--quiet", "identity").trim();
  if (!/^[a-f0-9]{64}$/u.test(identity)) {
    throw new Error("The owned identity container was not uniquely selected.");
  }
  const memory = command(
    "docker",
    ["inspect", "--format", "{{.HostConfig.Memory}}", identity],
    60_000,
  ).trim();
  if (memory !== "2147483648") {
    throw new Error("The owned identity container does not retain its memory bound.");
  }
};
const port = () =>
  new Promise((resolve, reject) => {
    const server = net.createServer();
    server.on("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const selected = server.address().port;
      server.close((error) => (error ? reject(error) : resolve(selected)));
    });
  });
const settings = () =>
  Object.fromEntries(
    readFileSync(path.join(configuration, "compose.env"), "utf8")
      .trim()
      .split("\n")
      .map((line) => {
        const position = line.indexOf("=");
        return [line.slice(0, position), line.slice(position + 1)];
      }),
  );
const ready = (address, ca) =>
  new Promise((resolve) => {
    const request = https.get(
      address,
      {
        ca,
        timeout: 2_000,
        family: 4,
        lookup: (_hostname, _options, callback) => callback(null, "127.0.0.1", 4),
      },
      (response) => {
        response.resume();
        response.once("end", () => resolve(response.statusCode === 200));
      },
    );
    request.once("error", () => resolve(false));
    request.once("timeout", () => request.destroy());
  });
const awaitReady = async (origin, ca, route = "/health/ready") => {
  const deadline = Date.now() + 240_000;
  while (Date.now() < deadline) {
    if (await ready(`${origin}${route}`, ca)) {
      return;
    }
    await new Promise((resolve) => {
      setTimeout(resolve, 300);
    });
  }
  throw new Error("Owned deployment readiness deadline.");
};
const priorRealmFile = "prior-claimcore-realm.json";
const priorRealmName = "Prior ClaimCore Realm";
const writePriorRealm = (origin, displayName) => {
  writeFileSync(
    path.join(owned, priorRealmFile),
    JSON.stringify({
      realm: "prior-claimcore",
      displayName,
      enabled: true,
      sslRequired: "all",
      clients: [
        {
          clientId: "prior-web",
          enabled: true,
          publicClient: true,
          standardFlowEnabled: true,
          redirectUris: [`${origin}/signin-oidc`],
        },
      ],
    }),
  );
  const mount = owned.replaceAll('"', '""');
  // configure-local assigns the import directory to the container UID.
  command(
    "docker",
    [
      "run",
      "--rm",
      "--network",
      "none",
      "--user",
      "0",
      "--mount",
      `type=bind,"source=${mount}",target=/work`,
      "--entrypoint",
      "/bin/sh",
      cleanupImage,
      "-ec",
      'install -m 0644 "/work/$1" "/work/$2/identity/realm/$1"',
      "write-prior-realm",
      priorRealmFile,
      path.basename(configuration),
    ],
    60_000,
  );
};
const verifyPriorRealm = async (browser, origin, provider) => {
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const login = new URL("/realms/prior-claimcore/protocol/openid-connect/auth", provider);
    login.search = new URLSearchParams({
      client_id: "prior-web",
      redirect_uri: `${origin}/signin-oidc`,
      response_type: "code",
      scope: "openid",
    }).toString();
    const page = await context.newPage();
    const response = await page.goto(login.href);
    expect(response?.status()).toBe(200);
    await expect(page.locator("#kc-header-wrapper")).toHaveText(priorRealmName);
    await expect(page.locator("#username")).toBeVisible();
    await expect(page.locator('link[href$="/css/claimcore.css"]')).toHaveCount(0);
  } finally {
    await context.close();
  }
};
const payload = (response) => {
  if (response.status() !== 200) {
    throw new Error("Unexpected owned service status.");
  }
  return response.json();
};
const session = async (context, origin, fingerprint) => {
  const value = await payload(
    await context.request.get(`${origin}/api/session`, {
      headers: { "ClaimCore-Contract": fingerprint },
    }),
  );
  if (
    value.outcome.kind !== "SUCCEEDED" ||
    !value.outcome.data.authenticated ||
    !value.outcome.data.antiforgeryToken
  ) {
    throw new Error("Owned session was not authenticated.");
  }
  return value.outcome.data.antiforgeryToken;
};
const post = (context, origin, endpoint, fingerprint, csrf) =>
  context.request.post(`${origin}/api/${endpoint}`, {
    headers: { "ClaimCore-Contract": fingerprint, "X-ClaimCore-Antiforgery": csrf, Origin: origin },
    data: {},
  });
const owner = async (context, origin, fingerprint, csrf) => {
  const value = await payload(await post(context, origin, "context", fingerprint, csrf));
  if (
    value.outcome.kind !== "SUCCEEDED" ||
    value.outcome.data.requester.kind !== "ACTIVE" ||
    !value.outcome.data.requester.capabilities.includes("MANAGE_ACCESS") ||
    !value.outcome.data.requester.capabilities.includes("REGISTER") ||
    !value.outcome.data.requester.capabilities.includes("RECORD_PAYMENT") ||
    !value.outcome.data.requester.capabilities.includes("STEWARD") ||
    value.outcome.data.requester.self.displayName.kind !== "NAMED" ||
    value.outcome.data.requester.self.displayName.value !== "Authored deployment owner"
  ) {
    throw new Error("Fresh owner people was not initialized with its evaluation roles.");
  }
  return value.outcome.data.requester;
};
const openProvider = async (page, origin, language) => {
  const address = new URL("/auth/login", origin);
  if (language !== undefined) {
    address.searchParams.set("language", language);
  }
  let response = await page.goto(address.href);
  if (response?.status() === 429) {
    // The evaluation image allows five challenges per one-minute window.
    // Keep the production limiter in the test path.
    await new Promise((resolve) => {
      setTimeout(resolve, 61_000);
    });
    response = await page.goto(address.href);
  }
  if (response?.status() !== 200) {
    throw new Error("A new provider challenge must reach its credential form.");
  }
};

const login = async (context, origin, provider) => {
  const page = await context.newPage();
  const preferences = {
    version: 3,
    language: "ar",
    displayLocale: "de-DE",
    mode: "system",
    accent: "blue",
  };
  await page.addInitScript(
    ({ appOrigin, preferred }) => {
      if (
        location.origin === appOrigin &&
        localStorage.getItem("claimcore.presentation.v3") === null
      ) {
        localStorage.setItem("claimcore.presentation.v3", JSON.stringify(preferred));
      }
    },
    { appOrigin: origin, preferred: preferences },
  );
  await page.goto(`${origin}/cases`);
  const signIn = page.locator(".login-link");
  await expect(signIn).toHaveAttribute("href", "/auth/login?language=ar");
  const challenge = page.waitForRequest((request) =>
    new URL(request.url()).pathname.endsWith("/protocol/openid-connect/auth"),
  );
  await signIn.click();
  const authorization = new URL((await challenge).url());
  expect(authorization.origin).toBe(provider);
  expect(authorization.searchParams.has("request_uri")).toBe(true);
  await page.waitForURL((url) => url.origin === provider);
  await expect(page.locator("html")).toHaveAttribute("lang", "ar");
  const selection = page.locator("#login-select-toggle");
  await expect(selection.locator("option")).toHaveCount(9);
  const latvian = await selection
    .locator("option")
    .evaluateAll((options) =>
      options
        .map((option) => option.value)
        .find((value) => new URL(value, location.origin).searchParams.get("kc_locale") === "lv"),
    );
  if (latvian === undefined) {
    throw new Error("The provider must offer an explicit Latvian locale.");
  }
  await selection.selectOption(latvian);
  await expect(page.locator("html")).toHaveAttribute("lang", "lv");
  process.stdout.write(
    "An Arabic app hint reached the provider through PAR. Explicit Latvian took precedence.\n",
  );
  await page.locator("#username").fill("owner");
  await page.locator("#password").fill("intentionally-wrong-synthetic-password");
  await page.locator("#kc-login").click();
  await expect(page.locator("#password")).toBeVisible();
  const denied = await payload(
    await context.request.get(`${origin}/api/session`, {
      headers: { "ClaimCore-Contract": consumerContractIdentity },
    }),
  );
  if (denied.outcome.kind !== "SUCCEEDED" || denied.outcome.data.authenticated) {
    throw new Error("The wrong synthetic credentials must leave the browser signed out.");
  }
  process.stdout.write("The provider refused the wrong synthetic credentials.\n");
  await page.locator("#username").fill("owner");
  const password = readFileSync(path.join(configuration, "owner-login.password"));
  try {
    await page.locator("#password").fill(password.toString("utf8").trim());
    await page.locator("#kc-login").click();
    await page.waitForURL(
      (url) => url.origin === origin && ["/", "/people", "/cases"].includes(url.pathname),
    );
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    expect(
      await page.evaluate(() => JSON.parse(localStorage.getItem("claimcore.presentation.v3"))),
    ).toEqual(preferences);
  } finally {
    password.fill(0);
    await page.close();
  }
};
const verifyProviderAppearance = async (page, dark) => {
  await page.keyboard.press("Tab");
  const username = page.locator("#username");
  await username.focus();
  await expect(username).toBeFocused();
  const appearance = await page.locator("#kc-login").evaluate((button) => ({
    token: getComputedStyle(document.documentElement).getPropertyValue("--cc-accent-solid").trim(),
    background: getComputedStyle(button).backgroundColor,
    font: getComputedStyle(button).fontFamily,
    headingFont: getComputedStyle(document.querySelector("#kc-header-wrapper")).fontFamily,
    focus: (() => {
      const field = document.querySelector("#username");
      const style = getComputedStyle(field);
      return [
        field.matches(":focus-visible"),
        style.outlineStyle,
        style.outlineWidth,
        style.outlineColor,
      ];
    })(),
    overflow: document.documentElement.scrollWidth > innerWidth,
    sheets: [...document.styleSheets].map(({ href }) => href),
  }));
  expect(appearance.token).toBe(dark ? "#a0c6fe" : "#0f52ab");
  expect(appearance.background).toBe(dark ? "rgb(160, 198, 254)" : "rgb(15, 82, 171)");
  expect(appearance.font).toMatch(/^system-ui,.*Noto Sans Arabic/u);
  expect(appearance.headingFont).toMatch(/^system-ui,.*Noto Sans Arabic/u);
  expect(appearance.focus).toEqual([
    true,
    "solid",
    "2px",
    dark ? "rgb(160, 198, 254)" : "rgb(32, 104, 203)",
  ]);
  if (
    appearance.overflow ||
    appearance.sheets.findIndex((url) => url?.endsWith("/css/styles.css")) < 0 ||
    appearance.sheets.findIndex((url) => url?.endsWith("/css/styles.css")) >=
      appearance.sheets.findIndex((url) => url?.endsWith("/css/claimcore.css"))
  ) {
    throw new Error("The inherited and branded provider styles must render in order.");
  }
};
const verifyProviderTheme = async (browser, origin, provider, languages) => {
  for (const [index, language] of languages.entries()) {
    // Do not overrun the real five-per-minute app challenge window in a clean installation.
    if (index === 5) {
      await new Promise((resolve) => {
        setTimeout(resolve, 61_000);
      });
    }
    const context = await browser.newContext({
      ignoreHTTPSErrors: true,
      locale: "en-US",
      viewport: { width: 320, height: 720 },
      colorScheme: language === "ar" ? "dark" : "light",
    });
    try {
      const page = await context.newPage();
      let brandedStyles = false;
      page.on("response", (response) => {
        const url = new URL(response.url());
        if (url.origin === provider && url.pathname.endsWith("/css/claimcore.css")) {
          brandedStyles = response.ok();
        }
      });
      // English in a fresh context is the no-hint control.
      await openProvider(page, origin, language === "en" ? undefined : language);
      if (!brandedStyles || new URL(page.url()).origin !== provider) {
        throw new Error(
          "The configured identity provider must serve the ClaimCore login stylesheet.",
        );
      }
      await expect(page.locator("html")).toHaveAttribute("lang", language);
      await expect(page.locator("html")).toHaveAttribute("dir", language === "ar" ? "rtl" : "ltr");
      await expect(page.locator("#login-select-toggle option")).toHaveCount(9);
      await expect(page.locator("#username")).toHaveAttribute("autocomplete", "username");
      await expect(page.locator("#password")).toHaveAttribute("autocomplete", "current-password");
      await expect(page.locator("#kc-login")).toBeVisible();
      await verifyProviderAppearance(page, language === "ar");
      process.stdout.write(
        `Rendered provider locale ${language} (form, direction, inherited and branded styles).\n`,
      );
    } finally {
      await context.close();
    }
  }
  process.stdout.write(`Rendered identity provider locales: ${languages.join(", ")}.\n`);
};
const qualify = async (origin, provider, ca) => {
  const fingerprint = consumerContractIdentity;
  if (!/^[0-9a-f]{64}$/u.test(fingerprint)) {
    throw new Error("Invalid generated consumer-contract identity.");
  }
  const browser = await chromium.launch({ headless: true });
  try {
    // Only this synthetic browser ignores fixture certificates. Readiness verifies the explicit CA and hostnames.
    const languages = presentationLanguages.filter(
      (language) => language !== presentationPseudolanguage,
    );
    await verifyProviderTheme(browser, origin, provider, languages);
    await verifyPriorRealm(browser, origin, provider);
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    await login(context, origin, provider);
    progress("owner-context");
    const csrf = await session(context, origin, fingerprint);
    const requester = await owner(context, origin, fingerprint, csrf);
    progress("first-case");
    const input = {
      operationId: operationId(),
      task: "case.open",
      binding: { kind: "CASE", expectedRevision: 0 },
      inputs: {
        "field:caseReference": "DEPLOYMENT-001",
        "field:incidentDate": requester.businessDate,
        "field:receivedDate": requester.businessDate,
        "field:incidentCountry": "LV",
        "input:parties": [
          {
            "input:partyRole": "CLAIMANT",
            "input:partyName": "Synthetic deployment claimant",
          },
          {
            "input:partyRole": "INSURER",
            "input:partyName": "Synthetic deployment insurer",
          },
        ],
        "field:claimedAmount": "100",
        "field:claimedCurrency": "EUR",
      },
    };
    const write = async (endpoint) =>
      payload(
        await context.request.post(`${origin}/api/${endpoint}`, {
          headers: {
            "ClaimCore-Contract": fingerprint,
            "ClaimCore-Installation": requester.installationId,
            "X-ClaimCore-Antiforgery": csrf,
            Origin: origin,
          },
          data: input,
        }),
      );
    if ((await write("operation.preview")).outcome.kind !== "REVIEWED") {
      throw new Error("The evaluation owner cannot review its first case.");
    }
    if ((await write("operation.execute")).outcome.kind !== "ACCEPTED") {
      throw new Error("The evaluation owner cannot open a case immediately after sign-in.");
    }
    progress("restart");
    writePriorRealm(origin, "Replacement realm import must be skipped");
    compose("restart", "identity", "web");
    await awaitReady(origin, ca);
    await awaitReady(provider, ca, "/realms/claimcore/.well-known/openid-configuration");
    await verifyProviderTheme(browser, origin, provider, languages);
    await verifyPriorRealm(browser, origin, provider);
    process.stdout.write(
      "The imported stock-theme realm retained its existing display name despite a conflicting import file.\n",
    );
    await owner(context, origin, fingerprint, csrf);
    await session(context, origin, fingerprint);
    progress("logout");
    await payload(await post(context, origin, "session.logout", fingerprint, csrf));
    const ended = await payload(
      await context.request.get(`${origin}/api/session`, {
        headers: { "ClaimCore-Contract": fingerprint },
      }),
    );
    if (ended.outcome.data.authenticated) {
      throw new Error("Owned logout retained its session.");
    }
    await context.close();
  } finally {
    await browser.close();
  }
};
const cleanup = () => {
  if (project === undefined && existsSync(path.join(configuration, "compose.env"))) {
    project = settings().CLAIMCORE_COMPOSE_PROJECT;
  }
  if (project === undefined) {
    return;
  }
  compose("down", "--volumes", "--remove-orphans", "--rmi", "local", "--timeout", "20");
  const values = settings();
  if (!/^[0-9]+$/u.test(values.CLAIMCORE_UID) || !/^[0-9]+$/u.test(values.CLAIMCORE_GID)) {
    throw new Error("Invalid generated cleanup identity.");
  }
  command(
    "docker",
    [
      "run",
      "--rm",
      "--name",
      `${project}-cleanup`,
      "--label",
      `claimcore.configuration=${project}`,
      "--network",
      "none",
      "--read-only",
      "--mount",
      `type=bind,"source=${configuration.replaceAll('"', '""')}",target=/config`,
      "--user",
      "0",
      "--entrypoint",
      "/bin/sh",
      cleanupImage,
      "-c",
      `chown -R ${values.CLAIMCORE_UID}:${values.CLAIMCORE_GID} /config`,
    ],
    60_000,
  );
};
let failed = false;
if (process.argv[4] === "cleanup") {
  try {
    cleanup();
  } catch {
    failed = true;
  }
  process.exit(failed ? 1 : 0);
}
try {
  progress("configure");
  const webPort = await port();
  let identityPort = await port();
  while (identityPort === webPort) {
    identityPort = await port();
  }
  command(
    "bash",
    [
      path.join(root, "deployment/configure-local.sh"),
      configuration,
      "Authored deployment owner",
      "Etc/UTC",
      String(webPort),
      String(identityPort),
    ],
    120_000,
  );
  project = settings().CLAIMCORE_COMPOSE_PROJECT;
  const origin = `https://localhost:${webPort}`;
  writePriorRealm(origin, priorRealmName);
  progress("build-and-start");
  command(
    "bash",
    [path.join(root, "eng/source-archive.sh"), path.join(root, "SOURCE.tar.gz")],
    120_000,
  );
  compose("build", "identity");
  progress("readiness-negative");
  rejectUnstartedIdentity();
  progress("build-and-start");
  compose("up", "--build", "--detach", "--wait", "--wait-timeout", "240");
  qualifyIdentityMemory();
  const ca = readFileSync(path.join(configuration, "ca.pem"));
  await awaitReady(origin, ca);
  const provider = `https://identity.localhost:${identityPort}`;
  await awaitReady(provider, ca, "/realms/claimcore/.well-known/openid-configuration");
  progress("owner-login");
  await qualify(origin, provider, ca);
  progress("qualified");
} catch {
  failed = true;
  process.stderr.write(`Fresh deployment qualification failed during ${stage}.\n`);
  if (project !== undefined) {
    try {
      compose("logs", "--no-color", "--timestamps");
      const container = compose("ps", "--all", "--quiet", "web").trim();
      if (/^[a-f0-9]{64}$/u.test(container)) {
        command("docker", ["inspect", "--format", "{{json .State}}", container], 60_000);
      }
    } catch {
      process.stderr.write("Owned deployment logs could not be retained.\n");
    }
  }
} finally {
  try {
    cleanup();
  } catch {
    failed = true;
    process.stderr.write("Fresh deployment cleanup failed for the owned project.\n");
  }
}
process.exitCode = failed ? 1 : 0;
