import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync, symlinkSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

const root = resolve(import.meta.dirname, "../../..");
/** @param {string} source @param {string} command @param {string[]} args */
function run(source, command, args) {
  const result = spawnSync(command, args, { cwd: source, encoding: "utf8", timeout: 600_000 });
  assert.equal(result.status, 0, "Real vocabulary producer/consumer probe failed.");
  return result.stdout;
}

const componentProbe = `import { expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { CaseList } from "../src/views/CaseList";
import { response } from "./v3-ui.fixtures";
it("propagates changed authoritative labels to real lookup, copy and navigation captions", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(response("case.list", "SUCCEEDED", {
    items: [{caseReference:"SYNTHETIC-PROBE",revision:"1",status:"OPENED"}],nextCursor:null
  })));
  render(<CaseList token="synthetic" onSelect={vi.fn()} onOpen={vi.fn()} />);
  expect(await screen.findByRole("button", {name:"Copy Synthetic reference label probe",exact:true})).toBeVisible();
  expect(screen.getByLabelText("Synthetic reference label probe (exact)", {exact:true})).toBeVisible();
  expect(screen.getByRole("button", {name:"Synthetic opening label probe",exact:true})).toBeVisible();
});
`;

/** The fixture already changed the Domain reference label and built a real CLI publication.
 * @param {string} source @param {string} publication
 */
export function verifyLabelChange(source, publication) {
  const commands = join(source, "src/ClaimCore.Domain/CommandDefinitions.fs");
  writeFileSync(
    commands,
    readFileSync(commands, "utf8").replace('"Open a case"', '"Synthetic opening label probe"'),
  );
  run(source, "dotnet", [
    "restore",
    "eng/ClaimCore.ContractGenerator/ClaimCore.ContractGenerator.fsproj",
    "--locked-mode",
  ]);
  symlinkSync(
    join(root, "web/node_modules"),
    join(source, "web/node_modules"),
    process.platform === "win32" ? "junction" : "dir",
  );
  run(source, "node", ["web/scripts/regenerate-contract.mjs", "--write-lock"]);
  run(source, "node", ["web/scripts/localization.mjs", "--write"]);
  run(source, "dotnet", [
    "publish",
    "src/ClaimCore.Cli/ClaimCore.Cli.fsproj",
    "--configuration",
    "Release",
    "--no-restore",
    "-p:UseAppHost=false",
    "--output",
    publication,
  ]);
  const assembly = join(publication, "ClaimCore.Cli.dll");
  const field = JSON.parse(
    run(source, "dotnet", [assembly, "describe", "fields", "caseReference"]),
  );
  const command = JSON.parse(run(source, "dotnet", [assembly, "describe", "commands", "OPEN"]));
  assert.equal(field.label, "Synthetic reference label probe");
  assert.equal(command.label, "Synthetic opening label probe");
  writeFileSync(join(source, "web/tests/authoritative-label-probe.test.tsx"), componentProbe);
  run(join(source, "web"), "node", [
    "node_modules/vitest/vitest.mjs",
    "run",
    "tests/authoritative-label-probe.test.tsx",
    "--coverage.enabled=false",
    "--reporter=default",
  ]);
}
