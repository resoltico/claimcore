// Run from a checkout with a matching prepared production Web build and installed Chromium.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import { cp, mkdtemp, readFile, readdir, rm, symlink, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "@playwright/test";

const root = fileURLToPath(new URL("../../../", import.meta.url));
const work = await mkdtemp(join(tmpdir(), "claimcore-catalog-cutover-"));
const oldBuild = join(work, "old");
const checkout = join(work, "checkout");
const prepared = join(checkout, "web/artifacts/catalogs");
const freshBuild = join(checkout, "web/dist");
const sha256 = (bytes) => createHash("sha256").update(bytes).digest("hex");

const run = (command, args, cwd) => {
  const result = spawnSync(command, args, { cwd, encoding: "utf8", maxBuffer: 16 * 1024 * 1024 });
  if (result.error !== undefined) {
    throw result.error;
  }
  return result;
};

const build = () =>
  run(
    join(root, "web/node_modules/.bin/vite"),
    ["build", "--configLoader", "runner"],
    join(checkout, "web"),
  );

const expectedFailure = (result, diagnostic) => {
  assert.notEqual(result.status, 0, "The negative build must fail");
  assert.match(result.stdout + result.stderr, diagnostic);
};

const readJson = async (path) => JSON.parse(await readFile(path, "utf8"));
const writeJson = (path, object) => writeFile(path, JSON.stringify(object));

async function buildCutoverPair() {
  const source = join(work, "source.tar.gz");
  assert.equal(run("bash", ["eng/source-archive.sh", source], root).status, 0);
  assert.equal(run("mkdir", ["-p", checkout], work).status, 0);
  assert.equal(run("tar", ["-xzf", source, "-C", checkout], work).status, 0);
  await symlink(join(root, "web/node_modules"), join(checkout, "web/node_modules"), "dir");
  await cp(join(root, "web/dist"), oldBuild, { recursive: true });
  await cp(join(root, "web/artifacts/catalogs"), prepared, { recursive: true });
  const manifestPath = join(prepared, "catalog-manifest.json");
  const original = await readJson(manifestPath);
  const old = original.en;
  const pair = await readJson(join(prepared, old.slice(1)));
  delete pair.chrome["ui.restartSignIn"];
  const altered = JSON.stringify(pair);
  const bad = `/assets/catalogs/en.${sha256(altered)}.json`;
  await writeFile(join(prepared, bad.slice(1)), altered);
  await writeJson(manifestPath, { ...original, en: bad });
  expectedFailure(build(), /Incomplete language catalog/u);
  pair.chrome["ui.restartSignIn"] = "Restart {";
  const incompleteTemplate = JSON.stringify(pair);
  const broken = `/assets/catalogs/en.${sha256(incompleteTemplate)}.json`;
  await writeFile(join(prepared, broken.slice(1)), incompleteTemplate);
  await writeJson(manifestPath, { ...original, en: broken });
  expectedFailure(build(), /Unterminated language argument/u);
  await writeJson(manifestPath, original);

  const newLabel = "Restart sign-in after the update";
  const locales = join(checkout, "web/locales");
  for (const file of await readdir(locales)) {
    if (!file.endsWith(".json")) {
      continue;
    }
    const path = join(locales, file);
    const catalog = await readJson(path);
    if (file === "en.json") {
      catalog["ui.restartSignIn"] = newLabel;
    } else {
      catalog["ui.restartSignIn"].source = sha256(newLabel).slice(0, 12);
    }
    await writeJson(path, catalog);
  }
  expectedFailure(build(), /Prepared browser catalog sources are stale/u);

  const prepare = run("bash", ["eng/prepare-browser-catalogs.sh"], checkout);
  assert.equal(prepare.status, 0, prepare.stderr || prepare.stdout);
  const resolved = build();
  assert.equal(resolved.status, 0, resolved.stderr || resolved.stdout);
  assert.equal(
    sha256(await readFile(join(root, "contracts/openapi.json"))),
    sha256(await readFile(join(checkout, "contracts/openapi.json"))),
  );
  const newManifest = await readJson(join(freshBuild, "catalog-manifest.json"));
  assert.notEqual(newManifest.en, old);
  return old;
}

async function browserCutover(oldCatalog) {
  let installed = oldBuild;
  let oldRequest = false;
  let sessions = 0;
  const media = {
    js: "text/javascript",
    json: "application/json",
    css: "text/css",
    html: "text/html",
  };
  const host = createServer((request, response) => {
    void (async () => {
      const path = new URL(request.url, "http://127.0.0.1").pathname;
      if (path === oldCatalog) {
        oldRequest = true;
        installed = freshBuild;
      }
      if (path === "/api/session") {
        sessions++;
        response.writeHead(503).end();
        return;
      }
      const file = path === "/" ? "/index.html" : path;
      const contents = await readFile(join(installed, file.slice(1)));
      response.setHeader(
        "Content-Type",
        media[file.split(".").at(-1)] ?? "application/octet-stream",
      );
      response.setHeader("Cache-Control", "no-store");
      response.end(contents);
    })().catch(() => {
      response.writeHead(404).end();
    });
  });
  const browser = await chromium.launch();
  try {
    await new Promise((yes) => {
      host.listen(0, "127.0.0.1", yes);
    });
    const page = await browser.newPage({ locale: "en-GB" });
    await page.goto(`http://127.0.0.1:${host.address().port}`);
    const reload = page.getByRole("button", { name: /reload/iu });
    await reload.waitFor({ timeout: 10000 });
    assert.equal(oldRequest, true);
    assert.equal(sessions, 0);
    const admitted = page.waitForResponse((response) => response.url().endsWith("/api/session"));
    await reload.click();
    await admitted;
    assert.equal(sessions, 1);
  } finally {
    await browser.close();
    host.close();
  }
}

try {
  const oldCatalog = await buildCutoverPair();
  await browserCutover(oldCatalog);
  process.stdout.write(
    "Catalog qualification passed: malformed shape, stale sources, and two-build HTTP reload.\n",
  );
} finally {
  await rm(work, { recursive: true, force: true });
}
