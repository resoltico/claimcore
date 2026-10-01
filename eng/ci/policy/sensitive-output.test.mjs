import assert from "node:assert/strict";
import { randomBytes, randomUUID } from "node:crypto";
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { assertNoSensitiveOutput } from "./sensitive-output.mjs";

/** @param {(directory: string) => void} body */
function inSandbox(body) {
  const directory = mkdtempSync(join(tmpdir(), "claimcore-sensitive-"));
  try {
    body(directory);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

test("text secrets are rejected in every encoding, clean output passes", () => {
  inSandbox((directory) => {
    const canary = `claimcore-sensitive-canary-${randomUUID()}`;
    const secret = join(directory, "private.secret");
    writeFileSync(secret, canary);
    const diagnostics = join(directory, "diagnostics");
    mkdirSync(diagnostics);
    const report = join(diagnostics, "output.txt");
    for (const variant of [
      canary,
      Buffer.from(canary).toString("base64"),
      encodeURIComponent(canary),
    ]) {
      writeFileSync(report, `prefix ${variant} suffix`);
      assert.throws(() => assertNoSensitiveOutput([diagnostics], [secret]), /rejected/u, variant);
    }
    writeFileSync(report, "bounded sanitized diagnostic");
    assert.equal(assertNoSensitiveOutput([diagnostics], [secret]), 1);
  });
});

test("binary secrets are rejected only through their encoded forms", () => {
  inSandbox((directory) => {
    const bytes = randomBytes(32);
    const secret = join(directory, "writer.capability");
    writeFileSync(secret, bytes);
    const diagnostics = join(directory, "diagnostics");
    mkdirSync(diagnostics);
    const report = join(diagnostics, "output.txt");
    for (const variant of [
      bytes.toString("base64"),
      bytes.toString("hex"),
      bytes.toString("hex").toUpperCase(),
    ]) {
      writeFileSync(report, variant);
      assert.throws(() => assertNoSensitiveOutput([diagnostics], [secret]), /rejected/u);
    }
    writeFileSync(report, "bounded sanitized diagnostic");
    assert.equal(assertNoSensitiveOutput([diagnostics], [secret]), 1);
  });
});

test("short secrets, link inputs and missing roots are refused", () => {
  inSandbox((directory) => {
    const short = join(directory, "short.secret");
    writeFileSync(short, "tiny");
    const diagnostics = join(directory, "diagnostics");
    mkdirSync(diagnostics);
    assert.throws(() => assertNoSensitiveOutput([diagnostics], [short]), /at least eight/u);
    const real = join(directory, "real.secret");
    writeFileSync(real, "long enough secret");
    symlinkSync(real, join(directory, "link.secret"));
    assert.throws(
      () => assertNoSensitiveOutput([diagnostics], [join(directory, "link.secret")]),
      /regular secret file/u,
    );
    symlinkSync(real, join(diagnostics, "link.txt"));
    assert.throws(() => assertNoSensitiveOutput([diagnostics], [real]), /link/u);
    assert.throws(() => assertNoSensitiveOutput([join(directory, "missing")], [real]), /./u);
  });
});
