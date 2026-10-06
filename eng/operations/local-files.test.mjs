import assert from "node:assert/strict";
import {
  chmodSync,
  lstatSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { emptyRoot } from "./local-files.mjs";
import { identityConfiguration } from "./local-identity.mjs";

test("configuration creation refuses occupied, broad, wrong-owner and linked roots", () => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-configuration-"));
  const link = `${root}-link`;
  try {
    chmodSync(root, 0o700);
    const owner = lstatSync(root).uid;
    emptyRoot(root, owner);
    assert.throws(() => emptyRoot(root, owner + 1));
    chmodSync(root, 0o755);
    assert.throws(() => emptyRoot(root, owner));
    chmodSync(root, 0o700);
    writeFileSync(join(root, "retained"), "original", { mode: 0o600 });
    assert.throws(() => emptyRoot(root, owner));
    assert.equal(readFileSync(join(root, "retained"), "utf8"), "original");
    symlinkSync(root, link);
    assert.throws(() => emptyRoot(link, owner));
  } finally {
    rmSync(link, { force: true });
    rmSync(root, { recursive: true, force: true });
  }
});

test("persistent local identities preserve PKCE and refuse password-grant shortcuts", () => {
  const configured = identityConfiguration();
  const cli = configured.realm.clients.find((client) => client.clientId === "claimcore-cli");
  const web = configured.realm.clients.find((client) => client.clientId === "claimcore-web");
  assert.equal(cli?.directAccessGrantsEnabled, false);
  assert.equal(web?.directAccessGrantsEnabled, false);
  assert.equal(cli?.attributes?.["pkce.code.challenge.method"], "S256");
  assert.equal(configured.realm.users[0]?.id, configured.ownerSubject);
  assert.equal(configured.realm.users[0]?.credentials[0]?.value, configured.ownerPassword);
  assert.notEqual(identityConfiguration().ownerSubject, configured.ownerSubject);
});
