import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { acquisitionDiagnostic, acquisitionStep } from "./tool-acquisition.mjs";
import { installTool } from "./tools.mjs";
import { scanArtifacts } from "./scan/artifacts.mjs";

const sensitive = "synthetic-private-path/provider-token";
/** @param {() => Promise<string> | string} acquire @param {string} target */
async function refused(acquire, target) {
  let output = "";
  const status = await scanArtifacts([target], {
    gitleaks: acquire,
    stderr: (text) => {
      output += text;
    },
  });
  assert.equal(status, 1);
  assert.doesNotMatch(output, /synthetic-private|provider-token/u);
  return output;
}

test("every owned acquisition phase fails closed without provider details", async () => {
  const root = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-acquisition-test-"));
  const target = join(root, "safe.txt");
  writeFileSync(target, "synthetic safe artifact");
  try {
    for (const phase of /** @type {import("./tool-acquisition.mjs").AcquisitionPhase[]} */ ([
      "MANIFEST",
      "CACHE",
      "DOWNLOAD",
      "INTEGRITY",
      "UNPACK",
      "PUBLISH",
    ])) {
      const output = await refused(
        () =>
          acquisitionStep(phase, () => {
            throw new Error(sensitive, { cause: new Error(sensitive) });
          }),
        target,
      );
      assert.equal(
        output,
        `Artifact secret scan could not complete at SCANNER_ACQUISITION (${phase}).\n`,
      );
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("unknown and forged acquisition errors disclose no diagnostic detail", async () => {
  const root = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-acquisition-test-"));
  const target = join(root, "safe.txt");
  writeFileSync(target, "synthetic safe artifact");
  try {
    const owned = await acquisitionStep("DOWNLOAD", () => {
      throw new Error(sensitive);
    }).catch((error) => error);
    const forged = Object.assign(Object.create(Object.getPrototypeOf(owned)), {
      phase: sensitive,
      message: sensitive,
    });
    for (const error of [
      forged,
      new Proxy(
        {},
        {
          getPrototypeOf: () => {
            throw new Error(sensitive);
          },
        },
      ),
      new Error(sensitive),
      { phase: sensitive },
      Object.assign(new Error(sensitive), { phase: "DOWNLOAD" }),
    ]) {
      assert.equal(acquisitionDiagnostic(error), "");
      assert.equal(
        await refused(() => {
          throw error;
        }, target),
        "Artifact secret scan could not complete at SCANNER_ACQUISITION.\n",
      );
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

/** @param {Buffer} bytes @param {"file" | "tar.gz"} archive */
function manifest(bytes, archive = "file") {
  return {
    probe: {
      version: "1",
      assets: {
        fixture: {
          url: sensitive,
          archive,
          sha256: createHash("sha256").update(bytes).digest("hex"),
        },
      },
    },
  };
}

test("installer reports the actual manifest, download, integrity, unpack and publication boundary", async () => {
  const root = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-acquisition-test-"));
  const bytes = Buffer.from("synthetic executable");
  const options = { tools: manifest(bytes), platform: "fixture", acquire: async () => bytes };
  try {
    await assert.rejects(
      installTool(root, "absent", options),
      (error) => acquisitionDiagnostic(error) === " (MANIFEST)",
    );
    await assert.rejects(
      installTool(root, "probe", {
        ...options,
        acquire: () => Promise.reject(new Error(sensitive)),
      }),
      (error) => acquisitionDiagnostic(error) === " (DOWNLOAD)",
    );
    await assert.rejects(
      installTool(root, "probe", { ...options, acquire: async () => Buffer.from("wrong") }),
      (error) => acquisitionDiagnostic(error) === " (INTEGRITY)",
    );
    await assert.rejects(
      installTool(root, "probe", { ...options, tools: manifest(bytes, "tar.gz") }),
      (error) => acquisitionDiagnostic(error) === " (UNPACK)",
    );
    writeFileSync(join(root, "artifacts"), "obstruct directory creation");
    await assert.rejects(
      installTool(root, "probe", options),
      (error) => acquisitionDiagnostic(error) === " (PUBLISH)",
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
