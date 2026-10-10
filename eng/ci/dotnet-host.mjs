// One physical SDK host owns composed commands, including native and nested producers.
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync, realpathSync } from "node:fs";
import { delimiter, dirname, isAbsolute, join, resolve } from "node:path";

/** @type {string | undefined} */
let admitted;

/** @returns {string} */
export function dotnetHost() {
  const host = nativeSdkHost(selectedHost());
  if (admitted !== undefined && admitted !== host) {
    throw new Error("The admitted .NET SDK host changed.");
  }
  if (admitted === undefined) {
    const expected = JSON.parse(
      readFileSync(resolve(import.meta.dirname, "../../global.json"), "utf8"),
    ).sdk.version;
    const probe = spawnSync(host, ["--version"], {
      cwd: resolve(import.meta.dirname, "../.."),
      encoding: "utf8",
      timeout: 30_000,
      maxBuffer: 4096,
    });
    if (probe.status !== 0 || probe.stdout.trim() !== expected) {
      throw new Error("The selected .NET SDK host does not match global.json.");
    }
    admitted = host;
  }
  return host;
}

/** PATH may name a package-manager wrapper; resolve its pinned physical SDK host.
 * @param {string} selected @returns {string} */
function nativeSdkHost(selected) {
  const expected = JSON.parse(
    readFileSync(resolve(import.meta.dirname, "../../global.json"), "utf8"),
  ).sdk.version;
  const result = spawnSync(selected, ["--list-sdks"], {
    encoding: "utf8",
    timeout: 30_000,
    maxBuffer: 16384,
  });
  if (result.status !== 0) {
    throw new Error("The selected physical SDK installation is unavailable.");
  }
  const pinned = result.stdout
    .trim()
    .split(/\r?\n/u)
    .map((line) => line.match(/^(\S+) \[(.+)\]$/u))
    .filter((match) => match?.[1] === expected);
  if (pinned.length !== 1 || !pinned[0]?.[2]) {
    throw new Error("The selected SDK installation does not uniquely provide the pin.");
  }
  return realpathSync(
    join(dirname(pinned[0][2]), process.platform === "win32" ? "dotnet.exe" : "dotnet"),
  );
}

/** Admission also fixes the native/nested environment before orchestration starts.
 * @returns {string} */
export function admitDotnetHost() {
  const host = dotnetHost();
  const root = dirname(host);
  for (const selector of [
    "DOTNET_HOST_PATH",
    "DOTNET_ROOT_ARM64",
    "DOTNET_ROOT_X64",
    "DOTNET_ROOT_X86",
  ]) {
    const supplied = process.env[selector];
    if (
      supplied &&
      realpathSync(
        selector === "DOTNET_HOST_PATH"
          ? supplied
          : join(supplied, process.platform === "win32" ? "dotnet.exe" : "dotnet"),
      ) !== host
    ) {
      throw new Error("Explicit .NET SDK selectors conflict.");
    }
  }
  requireMsbuildSelectors(root);
  process.env["CLAIMCORE_DOTNET"] = host;
  process.env["DOTNET_HOST_PATH"] = host;
  process.env["DOTNET_ROOT"] = root;
  process.env["PATH"] = `${root}${delimiter}${process.env["PATH"] ?? ""}`;
  return host;
}

/** @returns {string} */
function selectedHost() {
  const name = process.platform === "win32" ? "dotnet.exe" : "dotnet";
  const explicit = process.env["CLAIMCORE_DOTNET"];
  const root = process.env["DOTNET_ROOT"];
  if (explicit !== undefined && !isAbsolute(explicit)) {
    throw new Error("CLAIMCORE_DOTNET must select an absolute SDK host.");
  }
  const candidate =
    explicit ??
    (root
      ? join(root, name)
      : (process.env["PATH"] ?? "")
          .split(delimiter)
          .map((directory) => join(directory, name))
          .find(existsSync));
  if (!candidate || !existsSync(candidate)) {
    throw new Error("The selected .NET SDK host is unavailable.");
  }
  const host = realpathSync(candidate);
  if (root && realpathSync(join(root, name)) !== host) {
    throw new Error("Explicit .NET SDK selectors conflict.");
  }
  return host;
}

/** @param {string} root */
function requireMsbuildSelectors(root) {
  for (const selector of [
    "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR",
    "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER",
    "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
  ]) {
    if (process.env[selector]) {
      throw new Error("Custom SDK task and target resolver overrides are unsupported.");
    }
  }
  const {
    sdk: { version },
  } = JSON.parse(readFileSync(resolve(import.meta.dirname, "../../global.json"), "utf8"));
  for (const [selector, expected] of Object.entries({
    MSBuildSDKsPath: join(root, "sdk", version, "Sdks"),
    MSBUILD_EXE_PATH: join(root, "sdk", version, "MSBuild.dll"),
  })) {
    const supplied = process.env[selector];
    if (supplied && realpathSync(supplied) !== realpathSync(expected)) {
      throw new Error("Explicit MSBuild SDK selectors conflict.");
    }
  }
}
