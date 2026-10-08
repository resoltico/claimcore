import assert from "node:assert/strict";
import {
  closeSync,
  existsSync,
  ftruncateSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  openSync,
  readFileSync,
  realpathSync,
  renameSync,
  rmSync,
  symlinkSync,
  writeFileSync,
  writeSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { transferEvidence } from "./run-evidence.mjs";
import { fingerprint } from "./scan/files.mjs";

/** @param {(root:string, staging:string, destination:string)=>void} body */
function fixture(body) {
  const root = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-report-transfer-"));
  const staging = join(root, "qualified");
  mkdirSync(join(staging, "suite"), { recursive: true });
  writeFileSync(join(staging, "suite", "report.trx"), "synthetic qualified report\n");
  try {
    body(root, staging, join(root, "retained"));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("a report writer's held descriptor cannot alter its retained fresh-inode copy", () => {
  fixture((_root, staging, destination) => {
    const source = join(staging, "suite/report.trx");
    const fd = openSync(source, "r+");
    try {
      const qualified = fingerprint(staging);
      transferEvidence(staging, destination, qualified);
      const retained = join(destination, "suite/report.trx");
      const original = readFileSync(retained);
      assert.notEqual(lstatSync(retained).ino, lstatSync(source).ino);
      writeSync(fd, Buffer.from("changed by original writer"), 0, 26, 0);
      ftruncateSync(fd, 26);
      assert.notEqual(fingerprint(staging), qualified);
      assert.deepEqual(readFileSync(retained), original);
      assert.equal(fingerprint(destination), qualified);
    } finally {
      closeSync(fd);
    }
  });
});

test("changed qualified report bytes refuse before creating a retained tree", () => {
  fixture((_root, staging, destination) => {
    const qualified = fingerprint(staging);
    writeFileSync(join(staging, "suite/report.trx"), "different synthetic report");
    assert.throws(() => transferEvidence(staging, destination, qualified));
    assert.equal(existsSync(destination), false);
  });
});

test("relative fingerprints survive relocation but bind the report name", () => {
  fixture((_root, staging, destination) => {
    const qualified = fingerprint(staging);
    transferEvidence(staging, destination, qualified);
    assert.equal(fingerprint(destination), qualified);
    renameSync(join(destination, "suite/report.trx"), join(destination, "suite/other.trx"));
    assert.notEqual(fingerprint(destination), qualified);
  });
});

test("a stale retained destination is preserved and refused", () => {
  fixture((_root, staging, destination) => {
    mkdirSync(destination);
    writeFileSync(join(destination, "prior.txt"), "preserve prior evidence");
    assert.throws(() => transferEvidence(staging, destination, fingerprint(staging)));
    assert.equal(readFileSync(join(destination, "prior.txt"), "utf8"), "preserve prior evidence");
    assert.equal(existsSync(join(destination, "suite/report.trx")), false);
  });
});

test("retained destination links and linked parents never redirect a qualified copy", () => {
  fixture((root, staging, destination) => {
    const outside = join(root, "outside");
    mkdirSync(outside);
    symlinkSync(outside, destination, "junction");
    assert.throws(() => transferEvidence(staging, destination, fingerprint(staging)));
    assert.equal(existsSync(join(outside, "suite/report.trx")), false);
    const parent = join(root, "linked-parent");
    symlinkSync(outside, parent, "junction");
    assert.throws(() => transferEvidence(staging, join(parent, "fresh"), fingerprint(staging)));
    assert.equal(existsSync(join(outside, "fresh")), false);
  });
});
