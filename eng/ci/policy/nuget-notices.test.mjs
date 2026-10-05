import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { packageNoticeReader } from "./nuget-notices.mjs";

/** @param {(root: string, assets: string) => void} action */
function fixture(action) {
  const root = mkdtempSync(join(tmpdir(), "claimcore-package-notices-"));
  const cache = join(root, "packages");
  mkdirSync(join(cache, "example/1.0.0"), { recursive: true });
  const assets = join(root, "assets.json");
  writeFileSync(
    assets,
    JSON.stringify({
      libraries: { "Example/1.0.0": { type: "package", path: "example/1.0.0" } },
      packageFolders: { [cache]: {} },
    }),
  );
  try {
    action(root, assets);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}
const component = { name: "Example", version: "1.0.0", licenses: [{ license: { id: "MIT" } }] };

test("the restored package's license and embedded notices survive together", () => {
  fixture((root, assets) => {
    const path = join(root, "packages/example/1.0.0");
    writeFileSync(join(path, "LICENSE"), "Copyright Example owners\nMIT grant\n");
    writeFileSync(join(path, "NOTICE"), "Embedded BSD copyright and conditions\n");
    writeFileSync(join(path, "THIRD-PARTY-NOTICES.TXT"), "Additional attribution\n");
    const text = packageNoticeReader(assets)(component);
    assert.match(text, /Copyright Example owners/u);
    assert.match(text, /Embedded BSD copyright and conditions/u);
    assert.match(text, /Additional attribution/u);
    assert.doesNotMatch(text, /NET Foundation/u);
  });
});

test("expression-only MIT packages use grant text without invented owners", () => {
  fixture((_root, assets) => {
    const text = packageNoticeReader(assets)(component);
    assert.match(text, /Permission is hereby granted/u);
    assert.doesNotMatch(text, /Copyright|NET Foundation/u);
    assert.throws(
      () => packageNoticeReader(assets)({ ...component, licenses: [{ license: { id: "ISC" } }] }),
      /required license text/u,
    );
  });
});

test("missing versions, empty or malformed notices do not produce a legal bundle", () => {
  fixture((root, assets) => {
    const read = packageNoticeReader(assets);
    assert.throws(() => read({ ...component, version: "2.0.0" }), /absent from/u);
    const file = join(root, "packages/example/1.0.0/NOTICE");
    writeFileSync(file, "");
    assert.throws(() => read(component), /material is empty/u);
    writeFileSync(file, Buffer.from([0xff]));
    assert.throws(() => read(component), /encoded data/u);
  });
});
