// Platform-specific executable selection. Callers own environments and output disclosure.
import { existsSync } from "node:fs";
import { dirname, join, win32 } from "node:path";

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
  if (command === "powershell") {
    return win32.join(windowsSystemDirectory(), "WindowsPowerShell", "v1.0", "powershell.exe");
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
