import { readFile, writeFile, rename } from "node:fs/promises";
import { join } from "node:path";
import { randomUUID } from "node:crypto";
import { test as base } from "@playwright/test";

type RedirectEvidence = Readonly<{ originals: number; destinations: number; reply?: unknown }>;
type RedirectResponse = Readonly<{
  arm: (path: string, status: number) => Promise<void>;
  evidence: () => Promise<RedirectEvidence>;
}>;

// The fixture's HTTPS relay sends the original request to published Web, then replaces
// one reply. It counts destination requests independently of browser interception support.
export const test = base.extend<{ redirectResponse: RedirectResponse }>({
  redirectResponse: async ({ baseURL }, provide) => {
    const directory = process.env["CLAIMCORE_WEB_E2E_REDIRECT_STATE_DIR"];
    if (directory === undefined || baseURL === undefined) {
      throw new Error("Redirect fixture is missing.");
    }
    await provide({
      arm: async (path, status) => {
        const id = randomUUID();
        const temporary = join(directory, `redirect-control-${id}.json`);
        await writeFile(temporary, JSON.stringify({ id, path, status }), { mode: 0o600 });
        await rename(temporary, join(directory, "redirect-control.json"));
      },
      evidence: async () => {
        const value: unknown = JSON.parse(
          await readFile(join(directory, "redirect-evidence.json"), "utf8"),
        );
        if (
          typeof value !== "object" ||
          value === null ||
          !("originals" in value) ||
          typeof value.originals !== "number" ||
          !("destinations" in value) ||
          typeof value.destinations !== "number"
        ) {
          throw new Error("Redirect evidence is invalid.");
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
