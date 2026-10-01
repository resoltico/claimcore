// Platform-specific executable selection. Callers own environments and output disclosure.
import { existsSync } from "node:fs";
import { dirname, join } from "node:path";

/** @param {string} command @returns {string} */
export function executable(command) {
  if (command === "node") {
    return process.execPath;
  }
  if (command === "tar" && process.platform === "win32") {
    return join(process.env["SystemRoot"] ?? "C:\\Windows", "System32", "tar.exe");
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
