import { executable } from "../../eng/ci/executable.mjs";
import { execFile } from "node:child_process";
import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { isAbsolute, resolve } from "node:path";
import { promisify } from "node:util";

import { generateWebValidators } from "./generate-web-validators.mjs";

const execute = promisify(execFile);
const root = resolve(import.meta.dirname, "../..");
const generator = resolve(root, "eng/ClaimCore.ContractGenerator");

const requiredSdk = async () => {
  const globalJson = JSON.parse(await readFile(resolve(root, "global.json"), "utf8"));
  const version = globalJson.sdk?.version;
  if (typeof version !== "string" || version.length === 0) {
    throw new Error("The required .NET SDK version is missing.");
  }
  return version;
};

const selectedDotnet = () => {
  const configured = process.env["CLAIMCORE_DOTNET"];
  if (configured !== undefined && !isAbsolute(configured)) {
    throw new Error("CLAIMCORE_DOTNET must name one absolute executable.");
  }
  if (configured !== undefined && !existsSync(configured)) {
    throw new Error("The explicitly selected .NET executable is unavailable.");
  }
  return configured ?? executable("dotnet");
};

const resolveDotnet = async () => {
  const expected = await requiredSdk();
  const dotnet = selectedDotnet();
  try {
    const result = await execute(dotnet, ["--version"], { cwd: root });
    if (result.stdout.trim() === expected) {
      return dotnet;
    }
  } catch {
    // Host diagnostics can contain private paths; report only the supported configuration cause.
  }
  throw new Error("The exact .NET SDK selected by global.json is unavailable.");
};

/** @param {string} output */
export const generateContracts = async (output) => {
  const dotnet = await resolveDotnet();
  await execute(
    dotnet,
    [
      "run",
      "--project",
      generator,
      "--configuration",
      "Release",
      "--no-restore",
      "--",
      "--output",
      output,
    ],
    { cwd: root },
  );
  await generateWebValidators(output);
};
