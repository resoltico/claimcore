// Synthetic case and retained request make preservation comparisons non-vacuous.
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";

/** @param {import("@playwright/test").Page} page @param {{issuer: string, subject: string}} owner */
async function grantCasework(page, owner) {
  const granted = await page.evaluate(
    async ({ principal, eventId }) => {
      /** @param {unknown} value @returns {value is Record<string, unknown>} */
      const record = (value) =>
        value !== null && typeof value === "object" && !Array.isArray(value);
      /** @type {unknown} */
      const session = await (await fetch("/api/v3/session")).json();
      if (!record(session) || !record(session["outcome"]) || !record(session["outcome"]["data"])) {
        return false;
      }
      const token = session["outcome"]["data"]["antiforgeryToken"];
      if (typeof token !== "string") {
        return false;
      }
      const reply = await fetch("/api/v3/authority/grants/set", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-ClaimCore-Antiforgery": token },
        body: JSON.stringify({
          eventId,
          principal: { kind: "HUMAN", ...principal },
          role: "CASE_EDITOR",
          scope: { kind: "INSTALLATION" },
          active: true,
        }),
      });
      /** @type {unknown} */
      const result = await reply.json();
      return (
        reply.status === 200 &&
        record(result) &&
        result["endpoint"] === "authority.setGrant" &&
        record(result["outcome"]) &&
        result["outcome"]["tag"] === "APPLIED"
      );
    },
    { principal: owner, eventId: randomUUID() },
  );
  assert.equal(granted, true);
}
/** @param {import("@playwright/test").Page} page @param {{issuer: string, subject: string}} owner */
export async function seedCasework(page, owner) {
  await grantCasework(page, owner);
  await page.reload();
  await page.getByRole("button", { name: "Open a case", exact: true }).click();
  const values = {
    "Handler's case reference": "TLS-QUALIFICATION",
    "Incident date": "2026-01-01",
    "Incident notification date (FNOL)": "2026-01-02",
    "Country of incident": "Synthetic country",
    "Claimant name": "Synthetic claimant",
    "Responsible insurer": "Synthetic insurer",
    "Amount claimed": "100.0100",
    "Currency of claimed amount": "EUR",
  };
  for (const [label, value] of Object.entries(values)) {
    await page.getByLabel(label, { exact: true }).fill(value);
  }
  await page.getByRole("button", { name: "Review changes", exact: true }).click();
  await page.getByText("I confirm these changes.", { exact: true }).click();
  await page.getByRole("button", { name: "Record changes", exact: true }).click();
  await page.getByRole("heading", { name: "Recorded change", exact: true }).waitFor();
  await page.getByRole("button", { name: "Return to case", exact: true }).click();
  await page.getByRole("button", { name: /^Close the case:/u }).click();
  await page.getByRole("button", { name: "Review changes", exact: true }).click();
  await page
    .getByRole("button", { name: "Back to editing; keep for Recovery", exact: true })
    .click();
}
