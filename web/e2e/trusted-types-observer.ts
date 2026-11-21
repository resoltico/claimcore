import type { BrowserContext } from "@playwright/test";

export type TrustedTypesViolation = {
  directive: "require-trusted-types-for" | "trusted-types";
  sink: string | null;
  source: string | null;
  line: number;
  column: number;
};

export const observeTrustedTypes = async (
  context: BrowserContext,
  origin: string,
  violations: TrustedTypesViolation[],
  binding = "__claimcoreTrustedTypes",
): Promise<void> => {
  await context.exposeBinding(binding, (_, violation: TrustedTypesViolation) => {
    violations.push(violation);
  });
  await context.addInitScript(
    ({ origin: workspaceOrigin, binding: callbackName }) => {
      if (window.location.origin !== workspaceOrigin) {
        return;
      }
      window.addEventListener("securitypolicyviolation", (event) => {
        const directive = event.effectiveDirective;
        if (
          !event.isTrusted ||
          (directive !== "require-trusted-types-for" && directive !== "trusted-types")
        ) {
          return;
        }
        // Chromium can append the rejected input. Retain only a native DOM sink name.
        const candidate = event.sample.split("|", 1)[0]?.trim() ?? "";
        const sink = /^[A-Za-z][A-Za-z]+(?:[. ][A-Za-z][A-Za-z]+)?$/u.test(candidate)
          ? candidate
          : null;
        let source: string | null = null;
        if (event.sourceFile.startsWith(`${workspaceOrigin}/`)) {
          const basename = new URL(event.sourceFile).pathname.split("/").pop();
          if (basename !== undefined && /^[A-Za-z0-9_.-]+\.(?:js|mjs|html)$/u.test(basename)) {
            source = basename;
          }
        }
        const report = (
          window as unknown as Record<string, (event: TrustedTypesViolation) => void>
        )[callbackName];
        report?.({
          directive,
          sink,
          source,
          line: event.lineNumber,
          column: event.columnNumber,
        });
      });
    },
    { origin, binding },
  );
};
