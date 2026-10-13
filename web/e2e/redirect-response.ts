import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { randomUUID } from "node:crypto";
import { test as base } from "@playwright/test";

type RedirectEvidence = Readonly<{
  originals: number;
  destinations: number;
  reply?: unknown;
}>;
type RedirectResponse = Readonly<{
  arm: (path: string, status: number) => Promise<void>;
  evidence: () => Promise<RedirectEvidence>;
}>;

// The fixture proxy retains the native origin, TLS validation, cookies and CSP. Its private
// control file arms one exact synthetic request; it never receives a business/authority port.
export const test = base.extend<{ redirectResponse: RedirectResponse }>({
  redirectResponse: async ({ baseURL }, provide) => {
    const state = process.env["CLAIMCORE_WEB_E2E_REDIRECT_STATE_DIR"];
    if (state === undefined || baseURL === undefined) {
      throw new Error("E2E_REDIRECT_FIXTURE_CONFIGURATION_MISSING");
    }
    await provide({
      arm: async (path, status) => {
        await writeFile(
          join(state, "redirect-control.json"),
          JSON.stringify({ id: randomUUID(), path, status }),
          { mode: 0o600 },
        );
      },
      evidence: async () => {
        const value: unknown = JSON.parse(
          await readFile(join(state, "redirect-evidence.json"), "utf8"),
        );
        if (
          typeof value !== "object" ||
          value === null ||
          !("originals" in value) ||
          typeof value.originals !== "number" ||
          !("destinations" in value) ||
          typeof value.destinations !== "number"
        ) {
          throw new Error("E2E_REDIRECT_EVIDENCE_INVALID");
        }
        return {
          originals: value.originals,
          destinations: value.destinations,
          reply: "reply" in value ? value.reply : undefined,
        };
      },
    });
  },
});
