// Platform-specific executable selection. Callers own environments and output disclosure.
import { existsSync, lstatSync, realpathSync } from "node:fs";
import { dirname, join, win32 } from "node:path";
import { spawnSync } from "node:child_process";

export const minimumPowerShellMajor = 7;

/** The pinned Node API includes excludeNetwork, missing from its locked upstream typings.
 * @typedef {NodeJS.ProcessReport & {excludeNetwork:boolean}} NativeReport */

/** Native module paths come from the running pinned Node process, not caller environment.
 * @returns {unknown} */
function loadedLibraries() {
  const { report: declaredReport } = process;
  const report = /** @type {NativeReport} */ (declaredReport);
  if (typeof report.excludeEnv !== "boolean" || typeof report.excludeNetwork !== "boolean") {
    throw new Error("The pinned native Node diagnostic API is required.");
  }
  const previous = { env: report.excludeEnv, network: report.excludeNetwork };
  try {
    report.excludeEnv = true;
    report.excludeNetwork = true;
    return /** @type {{sharedObjects?:unknown}} */ (report.getReport()).sharedObjects;
  } finally {
    report.excludeEnv = previous.env;
    report.excludeNetwork = previous.network;
  }
}

/** @returns {string} */
function windowsSystemDirectory() {
  if (process.platform !== "win32") {
    throw new Error("Native Windows system directory requires Windows.");
  }
  const libraries = loadedLibraries();
  if (!Array.isArray(libraries) || libraries.some((path) => typeof path !== "string")) {
    throw new Error("Native Windows system modules are unavailable.");
  }
  const kernel = libraries.filter((path) => win32.basename(path).toLowerCase() === "kernel32.dll");
  const [path] = kernel;
  if (
    kernel.length !== 1 ||
    !path ||
    !win32.isAbsolute(path) ||
    win32.normalize(path) !== path ||
    win32.basename(win32.dirname(path)).toLowerCase() !== "system32"
  ) {
    throw new Error("Native Windows system module identity is unavailable.");
  }
  return win32.dirname(path);
}

/** @param {string} command @returns {string} */
export function executable(command) {
  if (command === "pwsh") {
    return win32.join(
      win32.parse(windowsSystemDirectory()).root,
      "Program Files",
      "PowerShell",
      "7",
      "pwsh.exe",
    );
  }
  if (command === "node") {
    return process.execPath;
  }
  if (command === "tar" && process.platform === "win32") {
    return win32.join(windowsSystemDirectory(), "tar.exe");
  }
  if (command === "dotnet" && process.env["DOTNET_ROOT"]) {
    const path = join(
      process.env["DOTNET_ROOT"],
      process.platform === "win32" ? "dotnet.exe" : "dotnet",
    );
    if (!existsSync(path)) {
      throw new Error("The explicitly selected .NET root has no executable.");
    }
    return path;
  }
  return command;
}

/** Resolve only modules shipped with the selected engine, never caller/user modules.
 * @param {NodeJS.ProcessEnv} [environment] @returns {NodeJS.ProcessEnv} */
export function powerShellModuleEnvironment(environment = process.env) {
  const native = executable("pwsh");
  return {
    ...Object.fromEntries(
      Object.entries(environment).filter(([key]) => key.toLowerCase() !== "psmodulepath"),
    ),
    PSModulePath: win32.join(win32.dirname(native), "Modules"),
  };
}

/** PowerShell startup can prefix user module paths; reset inside the selected child as well.
 * @param {string} source @returns {string[]} */
export function powerShellArguments(source) {
  return [
    "-NoLogo",
    "-NoProfile",
    "-NonInteractive",
    "-Command",
    `$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules'); ${source}`,
  ];
}

/** @param {unknown} observation @param {string} native @returns {string} */
export function requirePowerShellHost(observation, native) {
  const host = /** @type {{ major?:unknown,version?:unknown,home?:unknown }} */ (observation);
  if (
    !host ||
    typeof host !== "object" ||
    !Number.isInteger(host.major) ||
    /** @type {number} */ (host.major) < minimumPowerShellMajor ||
    typeof host.version !== "string" ||
    !/^\d+\.\d+\.\d+(?:\.\d+)?$/u.test(host.version) ||
    Number(host.version.split(".")[0]) !== host.major ||
    typeof host.home !== "string" ||
    win32.normalize(host.home).toLowerCase() !== win32.dirname(native).toLowerCase()
  ) {
    throw new Error(
      `PowerShell ${minimumPowerShellMajor}+ in the standard machine installation is required.`,
    );
  }
  return host.version;
}

/** @param {NodeJS.ProcessEnv} [environment] @returns {{ version:string,env:NodeJS.ProcessEnv }} */
export function powerShellHost(environment = process.env) {
  const native = executable("pwsh");
  if (!existsSync(native)) {
    throw new Error(
      `Install PowerShell ${minimumPowerShellMajor}+ using its standard machine MSI location.`,
    );
  }
  if (!lstatSync(native).isFile() || realpathSync(native).toLowerCase() !== native.toLowerCase()) {
    throw new Error("The selected PowerShell engine identity was refused.");
  }
  const env = powerShellModuleEnvironment(environment);
  const result = spawnSync(
    native,
    powerShellArguments(
      "[ordered]@{major=$PSVersionTable.PSVersion.Major;version=$PSVersionTable.PSVersion.ToString();home=$PSHOME} | ConvertTo-Json -Compress",
    ),
    { env, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], timeout: 30_000, maxBuffer: 4096 },
  );
  if (result.status !== 0) {
    throw new Error(
      `Install PowerShell ${minimumPowerShellMajor}+ using its standard machine MSI location.`,
    );
  }
  let observation;
  try {
    observation = JSON.parse(result.stdout);
  } catch {
    throw new Error("The selected PowerShell engine identity was refused.");
  }
  return { version: requirePowerShellHost(observation, native), env };
}

/** @param {NodeJS.ProcessEnv} [environment] @returns {NodeJS.ProcessEnv} */
export function powerShellEnvironment(environment = process.env) {
  return powerShellHost(environment).env;
}

/** @param {string} command @param {string[]} args @returns {[string, string[]]} */
export function commandLine(command, args) {
  if (process.platform === "win32" && (command === "npm" || command === "npx")) {
    const script = join(dirname(process.execPath), "node_modules/npm/bin", `${command}-cli.js`);
    if (!existsSync(script)) {
      throw new Error("The selected Node installation has no package-manager entry point.");
    }
    return [process.execPath, [script, ...args]];
  }
  return [executable(command), args];
}
