import assert from "node:assert/strict";
import test from "node:test";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { protectScratch, requirePrivateContext } from "./scratch-privacy.mjs";
import {
  executable,
  powerShellModuleEnvironment,
  requirePowerShellHost,
  minimumPowerShellMajor,
  powerShellHost,
  powerShellEnvironment,
  powerShellArguments,
} from "./executable.mjs";

/** Synthetic module observations exercise selection, not native Windows execution.
 * @param {unknown} libraries @param {()=>void} body */
function observation(libraries, body) {
  const platform = Object.getOwnPropertyDescriptor(process, "platform");
  assert.ok(platform);
  const { report: declaredReport } = process;
  const report = /** @type {import('./executable.mjs').NativeReport} */ (declaredReport);
  const original = report.getReport;
  const root = process.env["SystemRoot"];
  const flags = [report.excludeEnv, report.excludeNetwork];
  try {
    Object.defineProperty(process, "platform", { value: "win32" });
    process.env["SystemRoot"] = "Z:\\forged-system-root";
    report.getReport = () => {
      assert.equal(report.excludeEnv, true);
      assert.equal(report.excludeNetwork, true);
      return { sharedObjects: libraries };
    };
    body();
  } finally {
    report.getReport = original;
    Object.defineProperty(process, "platform", platform);
    if (root === undefined) {
      delete process.env["SystemRoot"];
    } else {
      process.env["SystemRoot"] = root;
    }
    assert.deepEqual([report.excludeEnv, report.excludeNetwork], flags);
  }
}

test("PowerShell selection follows the actual loaded system module, preserving drive and case", () => {
  observation(["D:\\Windows\\System32\\KERNEL32.DLL", "D:\\other\\library.dll"], () => {
    assert.equal(executable("pwsh"), "D:\\Program Files\\PowerShell\\7\\pwsh.exe");
    assert.equal(executable("node"), process.execPath);
    assert.equal(executable("tar"), "D:\\Windows\\System32\\tar.exe");
  });
  observation(["E:\\windows\\system32\\kernel32.dll"], () => {
    assert.equal(executable("pwsh"), "E:\\Program Files\\PowerShell\\7\\pwsh.exe");
  });
});

test("PowerShell module environment replaces every inherited casing with selected engine modules", () => {
  observation(["D:\\Windows\\System32\\kernel32.dll"], () => {
    assert.deepEqual(
      powerShellModuleEnvironment({
        PSModulePath: "PRIVATE-CORE-MODULES",
        psmodulepath: "PRIVATE-USER",
        OTHER: "kept",
      }),
      { PSModulePath: "D:\\Program Files\\PowerShell\\7\\Modules", OTHER: "kept" },
    );
  });
});

test("PowerShell engine admission requires the declared minimum and matching internal home", () => {
  assert.equal(minimumPowerShellMajor, 7);
  const native = "D:\\Program Files\\PowerShell\\7\\pwsh.exe";
  const host = { major: 7, version: "7.6.6", home: "D:\\Program Files\\PowerShell\\7" };
  assert.equal(requirePowerShellHost(host, native), "7.6.6");
  assert.equal(requirePowerShellHost({ ...host, major: 8, version: "8.0.0" }, native), "8.0.0");
  for (const refused of [
    null,
    [],
    { ...host, major: 5, version: "5.1.0" },
    { ...host, major: 6, version: "6.2.0" },
    { ...host, home: "D:\\PRIVATE-FORGED-HOME" },
    { ...host, version: "PRIVATE-VERSION" },
    { ...host, major: 7.5 },
    { ...host, version: "6.0.0" },
  ]) {
    assert.throws(
      () => requirePowerShellHost(refused, native),
      (error) => {
        assert.ok(error instanceof Error && !error.message.includes("PRIVATE"));
        return true;
      },
    );
  }
});

test("PowerShell refuses missing, ambiguous and non-system native module observations", () => {
  const failures = [
    undefined,
    [],
    [null],
    ["kernel32.dll"],
    ["D:\\private\\kernel32.dll"],
    ["D:\\Windows\\System32\\..\\System32\\kernel32.dll"],
    ["D:\\Windows\\System32\\kernel32.dll", "E:\\Windows\\System32\\kernel32.dll"],
  ];
  for (const libraries of failures) {
    observation(libraries, () => assert.throws(() => executable("pwsh")));
  }
  if (process.platform !== "win32") {
    assert.throws(() => executable("pwsh"), /requires Windows/u);
    assert.equal(executable("tar"), "tar");
  }
});

/** Native commands touch only one fresh synthetic root. @param {string} root @param {string} source */
function nativeCommand(root, source) {
  const result = spawnSync(executable("pwsh"), powerShellArguments(source), {
    env: { ...powerShellEnvironment(), CLAIMCORE_TEST_SCRATCH: root },
    stdio: "pipe",
  });
  assert.equal(result.status, 0, "Synthetic native module control must execute.");
}

/** @param {string} root */
function poisonedModule(root) {
  const module = join(root, "Microsoft.PowerShell.Security");
  mkdirSync(module);
  writeFileSync(
    join(module, "Microsoft.PowerShell.Security.psm1"),
    "function Get-Acl { [IO.File]::WriteAllText((Join-Path $env:CLAIMCORE_TEST_SCRATCH 'module-loaded.fixture'),'synthetic'); throw 'PRIVATE-MODULE-PAYLOAD' }",
  );
}

test("native PowerShell ACL APIs ignore hostile inherited module locations", () => {
  if (process.platform !== "win32") {
    return;
  }
  process.stdout.write(`Native PowerShell ${powerShellHost().version}\n`);
  const root = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-native-modules-"));
  const original = process.env["PSModulePath"];
  try {
    poisonedModule(root);
    const marker = join(root, "module-loaded.fixture");
    nativeCommand(
      root,
      "$env:PSModulePath=$env:CLAIMCORE_TEST_SCRATCH; Import-Module Microsoft.PowerShell.Security -Force; try { Get-Acl } catch {}",
    );
    assert.equal(existsSync(marker), true);
    rmSync(marker);
    process.env["PSModulePath"] = root;
    nativeCommand(root, "Get-Acl -LiteralPath $env:CLAIMCORE_TEST_SCRATCH | Out-Null");
    const owned = join(root, "owned");
    mkdirSync(owned);
    protectScratch(owned);
    writeFileSync(join(owned, "context.json"), "synthetic");
    requirePrivateContext(join(owned, "context.json"));
    assert.equal(existsSync(marker), false);
  } finally {
    if (original === undefined) {
      delete process.env["PSModulePath"];
    } else {
      process.env["PSModulePath"] = original;
    }
    rmSync(root, { recursive: true, force: true });
  }
});
