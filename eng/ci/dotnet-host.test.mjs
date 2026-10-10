import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  constants,
  cpSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { admitDotnetHost } from "./dotnet-host.mjs";

const root = resolve(import.meta.dirname, "../..");
const selected = admitDotnetHost();
const {
  sdk: { version },
} = JSON.parse(readFileSync(join(root, "global.json"), "utf8"));
const moduleUrl = new URL("./dotnet-host.mjs", import.meta.url).href;

/** @param {string} script @param {NodeJS.ProcessEnv} env */
function probe(script, env) {
  return spawnSync(process.execPath, ["--input-type=module", "-e", script], {
    cwd: root,
    env,
    encoding: "utf8",
    timeout: 60_000,
    maxBuffer: 8192,
  });
}

/** @param {string} original @param {string} copied */
function copySdk(original, copied) {
  for (const path of [
    `dotnet${process.platform === "win32" ? ".exe" : ""}`,
    "host",
    "shared",
    `sdk/${version}`,
    "packs",
  ]) {
    cpSync(join(original, path), join(copied, path), {
      recursive: true,
      dereference: true,
      mode: constants.COPYFILE_FICLONE,
    });
  }
}

test("two physical pinned SDK hosts cannot redirect nested selection through PATH or conflicting selectors", () => {
  const scratch = realpathSync(mkdtempSync(join(tmpdir(), "claimcore-sdk-host-")));
  const copied = join(scratch, "sdk");
  const original = dirname(selected);
  try {
    copySdk(original, copied);
    const host = join(copied, process.platform === "win32" ? "dotnet.exe" : "dotnet");
    const project = join(scratch, "probe.proj");
    writeFileSync(project, "<Project />");
    const env = {
      ...process.env,
      CLAIMCORE_DOTNET: host,
      DOTNET_ROOT: copied,
      DOTNET_HOST_PATH: host,
      DOTNET_ROOT_ARM64: copied,
      DOTNET_ROOT_X64: copied,
      DOTNET_ROOT_X86: copied,
      DOTNET_CLI_HOME: join(scratch, "home"),
      PATH: `${original}${process.platform === "win32" ? ";" : ":"}${process.env.PATH ?? ""}`,
    };
    const script = `import {admitDotnetHost} from ${JSON.stringify(moduleUrl)}; import {spawnSync} from 'node:child_process'; const host=admitDotnetHost(); const result=spawnSync('dotnet',['msbuild',${JSON.stringify(project)},'-nologo','-getProperty:MSBuildToolsPath'],{encoding:'utf8'}); console.log(JSON.stringify({host,status:result.status,tools:result.stdout.trim()}));`;
    const result = probe(script, env);
    assert.equal(result.status, 0, "Physical SDK selection probe must complete");
    const observed = JSON.parse(result.stdout);
    assert.equal(observed.host, host);
    assert.equal(observed.status, 0);
    assert.equal(
      observed.tools.replaceAll("\\", "/").toLowerCase(),
      join(copied, "sdk", version).replaceAll("\\", "/").toLowerCase(),
    );
    requireConflictingSelectorsRefuse(env, original);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test("SDK admission accepts the pinned entry before another CRLF entry", () => {
  const script = `import cp from 'node:child_process'; import {syncBuiltinESMExports} from 'node:module'; const spawn=cp.spawnSync; cp.spawnSync=(host,args,options)=>args[0]==='--list-sdks'?{status:0,stdout:${JSON.stringify(`${version} [${join(dirname(selected), "sdk")}]\r\n99.0.0 [synthetic]\r\n`)}}:spawn(host,args,options); syncBuiltinESMExports(); const {admitDotnetHost}=await import(${JSON.stringify(moduleUrl)}); console.log(admitDotnetHost());`;
  const result = probe(script, process.env);
  assert.equal(result.status, 0);
  assert.equal(result.stdout.trim(), selected);
});

/** @param {NodeJS.ProcessEnv} env @param {string} original */
function requireConflictingSelectorsRefuse(env, original) {
  for (const selector of [
    { DOTNET_HOST_PATH: selected },
    { DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR: original },
    { DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER: "99.0.0" },
    { DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR: original },
    { MSBuildSDKsPath: join(original, "sdk", version, "Sdks") },
    { MSBUILD_EXE_PATH: join(original, "sdk", version, "MSBuild.dll") },
  ]) {
    const refused = probe(
      `import {admitDotnetHost} from ${JSON.stringify(moduleUrl)}; try {admitDotnetHost(); process.exitCode=2;} catch {process.exitCode=0;}`,
      { ...env, ...selector },
    );
    assert.equal(refused.status, 0, "A same-pin conflicting physical selector must refuse");
  }
}
