import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  chmodSync,
  copyFileSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { checkPowerShell } from "../lint/powershell.mjs";
import { repositoryFiles } from "./repository.mjs";
import test from "node:test";
import { protectScratch, requirePrivateContext } from "./scratch-privacy.mjs";
import { executable, powerShellEnvironment, powerShellArguments } from "./executable.mjs";

/** @param {(root:string)=>void} body */
function fixture(body) {
  const root = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-private-scratch-"));
  try {
    body(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}
/** Mutations affect only a freshly created synthetic root.
 * @param {string} root @param {string} command */
function windowsMutation(root, command) {
  const result = spawnSync(
    executable("pwsh"),
    powerShellArguments(
      `$ErrorActionPreference = 'Stop'; $root = $env:CLAIMCORE_TEST_SCRATCH; ${command}`,
    ),
    { env: { ...powerShellEnvironment(), CLAIMCORE_TEST_SCRATCH: root }, stdio: "pipe" },
  );
  assert.equal(result.status, 0, "Synthetic ACL mutation must execute before the refusal oracle.");
}

/** Only fresh synthetic entries receive ACL/xattr mutations.
 * @param {string} command @param {string[]} args */
function macMutation(command, args) {
  const result = spawnSync(command, args, { stdio: "pipe", timeout: 30_000 });
  assert.equal(result.status, 0, "Synthetic macOS metadata mutation must succeed.");
}

/** @param {()=>void} operation @param {string} privatePath */
function privateRefusal(operation, privatePath) {
  assert.throws(operation, (error) => {
    assert.ok(error instanceof Error);
    assert.match(error.message, /Private orchestration/u);
    assert.ok(!error.message.includes(privatePath));
    assert.ok(!error.message.includes("synthetic-private-context"));
    return true;
  });
}

test("native orchestration scratch protects a fresh root and admits only its private regular context", () => {
  fixture((root) => {
    protectScratch(root);
    const path = join(root, "context.json");
    writeFileSync(path, '{"synthetic":true}\n', { mode: 0o600 });
    requirePrivateContext(path);
    assert.throws(() => protectScratch(root));
    writeFileSync(join(root, "other.json"), "synthetic", { mode: 0o600 });
    if (process.platform === "win32") {
      assert.throws(() => requirePrivateContext(join(root, "other.json")));
    } else {
      chmodSync(path, 0o644);
      assert.throws(() => requirePrivateContext(path));
    }
  });
});

test("native orchestration privacy refuses linked or substituted scratch and context entries", () => {
  fixture((parent) => {
    const root = join(parent, "owned");
    mkdirSync(root, { mode: 0o700 });
    protectScratch(root);
    const context = join(root, "context.json");
    writeFileSync(context, "synthetic", { mode: 0o600 });
    const linked = join(parent, "linked");
    symlinkSync(root, linked, "junction");
    assert.throws(() => protectScratch(linked));
    assert.throws(() => requirePrivateContext(join(linked, "context.json")));
    rmSync(context);
    const other = join(parent, "private.fixture");
    writeFileSync(other, "private fixture", { mode: 0o600 });
    symlinkSync(other, context);
    assert.throws(() => requirePrivateContext(context));
  });
});

test("native macOS metadata admits private extended attributes but refuses ACL grants hidden by mode bits", () => {
  if (process.platform !== "darwin") {
    return;
  }
  fixture((root) => {
    macMutation("/usr/bin/xattr", ["-w", "com.claimcore.synthetic", "synthetic", root]);
    protectScratch(root);
    const context = join(root, "context.json");
    writeFileSync(context, "synthetic-private-context", { mode: 0o600 });
    macMutation("/usr/bin/xattr", ["-w", "com.claimcore.synthetic", "synthetic", context]);
    requirePrivateContext(context);
    macMutation("/bin/chmod", [
      "+a",
      "everyone allow read,readattr,readextattr,readsecurity",
      context,
    ]);
    assert.equal(lstatSync(context).mode & 0o777, 0o600);
    privateRefusal(() => requirePrivateContext(context), root);
  });
});

test("native macOS scratch ACL admission refuses initial grants and rechecks a later parent grant", () => {
  if (process.platform !== "darwin") {
    return;
  }
  const acl = "everyone allow list,search,readattr,readextattr,readsecurity";
  fixture((root) => {
    macMutation("/bin/chmod", ["+a", acl, root]);
    assert.equal(lstatSync(root).mode & 0o777, 0o700);
    privateRefusal(() => protectScratch(root), root);
  });
  fixture((root) => {
    protectScratch(root);
    const context = join(root, "context.json");
    writeFileSync(context, "synthetic-private-context", { mode: 0o600 });
    requirePrivateContext(context);
    macMutation("/bin/chmod", ["+a", acl, root]);
    assert.equal(lstatSync(root).mode & 0o777, 0o700);
    privateRefusal(() => requirePrivateContext(context), root);
  });
});

test("Windows ACL readback rejects broad principals and broken effective inheritance", () => {
  if (process.platform !== "win32") {
    // Windows owns the DACL oracle; other systems still exercise actual mode refusal.
    fixture((root) => {
      chmodSync(root, 0o755);
      assert.throws(() => protectScratch(root));
    });
    return;
  }
  fixture((parent) => {
    const source = join(parent, "public.fixture");
    writeFileSync(source, "synthetic public source");
    windowsMutation(
      parent,
      "$path=Join-Path $root 'public.fixture'; $acl=Get-Acl -LiteralPath $path; $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new([System.Security.Principal.SecurityIdentifier]::new('S-1-1-0'),'Read','Allow')); Set-Acl -LiteralPath $path -AclObject $acl",
    );
    const root = join(parent, "private-copy");
    mkdirSync(root);
    protectScratch(root);
    const copied = join(root, "context.json");
    copyFileSync(source, copied);
    requirePrivateContext(copied);
  });
  const modifications = [
    "$acl=Get-Acl -LiteralPath $root; $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new([System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),'Read','ContainerInherit,ObjectInherit','None','Allow')); Set-Acl -LiteralPath $root -AclObject $acl",
    "$acl=Get-Acl -LiteralPath $root; $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new([System.Security.Principal.SecurityIdentifier]::new('S-1-1-0'),'Read','ContainerInherit,ObjectInherit','None','Allow')); Set-Acl -LiteralPath $root -AclObject $acl",
    "$acl=Get-Acl -LiteralPath $root; $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new([System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'Read','ContainerInherit,ObjectInherit','None','Allow')); Set-Acl -LiteralPath $root -AclObject $acl",
    "$acl=Get-Acl -LiteralPath $root; $acl.SetAccessRuleProtection($false,$true); Set-Acl -LiteralPath $root -AclObject $acl",
    "$path=Join-Path $root 'context.json'; $acl=Get-Acl -LiteralPath $path; $acl.SetAccessRuleProtection($true,$true); Set-Acl -LiteralPath $path -AclObject $acl",
    "$path=Join-Path $root 'context.json'; $acl=Get-Acl -LiteralPath $path; $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new([System.Security.Principal.SecurityIdentifier]::new('S-1-1-0'),'Read','Allow')); Set-Acl -LiteralPath $path -AclObject $acl",
  ];
  for (const command of modifications) {
    fixture((root) => {
      protectScratch(root);
      writeFileSync(join(root, "context.json"), "synthetic", { mode: 0o600 });
      requirePrivateContext(join(root, "context.json"));
      windowsMutation(root, command);
      assert.throws(() => requirePrivateContext(join(root, "context.json")));
    });
  }
});

test("native PowerShell AST gate measures source and refuses oversized or branch-heavy executable units", () => {
  const root = resolve(import.meta.dirname, "../..");
  if (process.platform !== "win32") {
    assert.throws(() => checkPowerShell([]), /requires Windows/u);
    return;
  }
  checkPowerShell(
    repositoryFiles(root)
      .filter((path) => path.endsWith(".ps1"))
      .map((path) => join(root, path)),
  );
  fixture((scratch) => {
    const path = join(scratch, "fixture.ps1");
    const failures = [
      `function Oversized {\n${"  Write-Output 'synthetic'\n".repeat(51)}}`,
      `function Branches {\n${"  if ($true) { Write-Output 'synthetic' }\n".repeat(13)}}`,
      "function Arguments($a,$b,$c,$d,$e,$f) { Write-Output 'synthetic' }",
      "if ($true) {",
      "[Diagnostics.CodeAnalysis.SuppressMessageAttribute('Test','Test')] param()",
      "Write-Output 'synthetic'\n".repeat(51),
      `begin {\n${"  Write-Output 'synthetic'\n".repeat(51)}}`,
      `process {\n${"  Write-Output 'synthetic'\n".repeat(51)}}`,
      `dynamicparam {\n${"  Write-Output 'synthetic'\n".repeat(51)}}`,
      `class Oversized { [void] Run() {\n${"  Write-Output 'synthetic'\n".repeat(51)}} }`,
      `class Branches { [void] Run() {\n${"  if ($true) { Write-Output 'synthetic' }\n".repeat(13)}} }`,
    ];
    for (const source of failures) {
      writeFileSync(path, source);
      assert.throws(() => checkPowerShell([path]));
    }
    writeFileSync(
      path,
      "function Bounded($value) { if ($value) { Write-Output 'synthetic' } }\nBounded $true\n",
    );
    checkPowerShell([path]);
    writeFileSync(
      path,
      "begin { Write-Output 'synthetic' } process { if ($true) { Write-Output 'synthetic' } } end { Write-Output 'synthetic' }\n",
    );
    checkPowerShell([path]);
    writeFileSync(
      path,
      "class Bounded { [void] Run() { if ($true) { Write-Output 'synthetic' } } }\n",
    );
    checkPowerShell([path]);
  });
});

test("native Windows ACL refusal uses the loaded system binary despite a forged SystemRoot", () => {
  if (process.platform !== "win32") {
    assert.throws(() => executable("pwsh"), /requires Windows/u);
    return;
  }
  const original = process.env["SystemRoot"];
  const trusted = executable("pwsh");
  const tar = executable("tar");
  assert.equal(spawnSync(tar, ["--version"], { stdio: "pipe" }).status, 0);
  fixture((root) => {
    protectScratch(root);
    const context = join(root, "context.json");
    writeFileSync(context, "synthetic", { mode: 0o600 });
    windowsMutation(
      root,
      "$path=Join-Path $root 'context.json'; $acl=Get-Acl -LiteralPath $path; $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new([System.Security.Principal.SecurityIdentifier]::new('S-1-1-0'),'Read','Allow')); Set-Acl -LiteralPath $path -AclObject $acl",
    );
    try {
      process.env["SystemRoot"] = join(root, "forged-system-root");
      assert.equal(executable("pwsh"), trusted);
      assert.equal(executable("tar"), tar);
      assert.throws(() => requirePrivateContext(context));
    } finally {
      if (original === undefined) {
        delete process.env["SystemRoot"];
      } else {
        process.env["SystemRoot"] = original;
      }
    }
  });
});
