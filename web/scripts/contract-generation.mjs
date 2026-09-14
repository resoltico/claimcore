import { execFile } from "node:child_process";
import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { homedir } from "node:os";
import { isAbsolute, join, resolve } from "node:path";
import { promisify } from "node:util";

import { formatProtocolSources } from "./protocol-generation.mjs";

import { generateWebValidators } from "./generate-web-validators.mjs";

const execute = promisify(execFile);
const root = resolve(import.meta.dirname, "../..");
const generator = resolve(root, "eng/ClaimCore.ContractGenerator");

const requiredSdk = async () => {
  const globalJson = JSON.parse(await readFile(resolve(root, "global.json"), "utf8"));
  const version = globalJson.sdk?.version;
  if (typeof version !== "string" || version.length === 0)
    throw new Error("The required .NET SDK version is missing.");
  return version;
};

const dotnetCandidates = () => {
  const executable = process.platform === "win32" ? "dotnet.exe" : "dotnet";
  const configured = process.env["CLAIMCORE_DOTNET"];
  if (configured !== undefined && !isAbsolute(configured))
    throw new Error("CLAIMCORE_DOTNET must name one absolute executable.");
  return [configured, join(homedir(), ".dotnet", executable), executable].filter(
    (candidate) => candidate !== undefined && (!isAbsolute(candidate) || existsSync(candidate)),
  );
};

const resolveDotnet = async () => {
  const expected = await requiredSdk();
  for (const candidate of dotnetCandidates()) {
    try {
      const result = await execute(candidate, ["--version"], { cwd: root });
      if (result.stdout.trim() === expected) return candidate;
    } catch {
      // Try the next bounded candidate without exposing host-specific diagnostics.
    }
  }
  throw new Error("The exact .NET SDK selected by global.json is unavailable.");
};

export const generateConvergenceContracts = async (output, protocolOutput) => {
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
      ...(protocolOutput === undefined ? [] : ["--protocol-output", protocolOutput]),
    ],
    { cwd: root },
  );
  await generateWebValidators(output);
  if (protocolOutput !== undefined)
    await formatProtocolSources(
      protocolOutput,
      async (directory) => {
        await execute(dotnet, ["fantomas", directory], { cwd: root });
      },
      join(root, ".editorconfig"),
    );
};
