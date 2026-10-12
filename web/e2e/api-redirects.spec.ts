import { randomUUID } from "node:crypto";
import { expect, type Page } from "@playwright/test";
import { test } from "./redirect-response";
import { openCase, startCommand, prepare, keepForRecovery } from "./case-workflow";
import { isWebV3Response } from "../src/generated/contracts/web-v3.validation";
import type { WebV3Response } from "../src/generated/contracts/web-v3.types";
import { openAuthenticated } from "./session-helpers";

const destination = (page: Page) => {
  let requests = 0;
  const { origin } = new URL(page.url());
  page.on("request", (request) => {
    const target = new URL(request.url());
    if (target.origin === origin && target.pathname === "/__synthetic/redirect-destination") {
      requests += 1;
    }
  });
  return () => requests;
};

for (const status of [301, 302, 303, 307, 308]) {
  test(`refuses ordinary API ${status} after original acceptance without redirect dispatch [CC-WEB-001]`, async ({
    page,
    redirectResponse,
  }) => {
    await openAuthenticated(page);
    await openCase(page, `REDIRECT-${randomUUID()}`);
    await startCommand(page, "Close the case");
    const identity = await prepare(page);
    const requests = destination(page);
    let original = 0;
    let committed = false;
    await page.route("**/api/v3/operations/submit", async (route) => {
      original += 1;
      const response = await route.fetch();
      const payload: unknown = await response.json();
      if (response.status() === 200 && (await isWebV3Response("command.execute", payload))) {
        const { outcome } = payload as WebV3Response<"command.execute">;
        committed = outcome.tag === "COMPLETED" && outcome.data.execution.tag === "ACCEPTED";
      }
      await route.continue({ url: redirectResponse.url(status) });
    });
    await page.getByText("I confirm these changes.", { exact: true }).click();
    await page.getByRole("button", { name: "Record changes" }).click();
    await expect(page.getByRole("alert")).toContainText("Recovery");
    expect(original).toBe(1);
    expect(committed).toBe(true);
    expect(redirectResponse.requests()).toBe(1);
    expect(requests()).toBe(0);
    await page.unroute("**/api/v3/operations/submit");
    await expect(page.getByRole("heading", { name: "Recovery", exact: true })).toBeVisible();
    await expect(page.getByRole("alert")).toContainText(identity.operationId);
    await expect(page.getByRole("alert")).toContainText(identity.requestSha256);
    await page.getByRole("button", { name: "Operations", exact: true }).click();
    await expect(page.getByLabel("Exact operation ID")).toHaveValue(identity.operationId);
    await page.getByRole("button", { name: "Look up recorded result" }).click();
    await expect(
      page.getByText(`Accepted operation ${identity.operationId}`, { exact: false }),
    ).toBeVisible();
    expect(original).toBe(1);
    expect(requests()).toBe(0);
  });

  test(`refuses recovery export ${status} before a second destination request [CC-REC-001]`, async ({
    page,
    redirectResponse,
  }) => {
    await openAuthenticated(page);
    await openCase(page, `EXPORT-REDIRECT-${randomUUID()}`);
    await startCommand(page, "Close the case");
    const identity = await prepare(page);
    await keepForRecovery(page);
    await page.getByRole("button", { name: "Recovery", exact: true }).click();
    const row = page.locator(".recovery-list li").filter({ hasText: identity.operationId });
    await row.getByRole("button", { name: "Inspect", exact: true }).click();
    const requests = destination(page);
    let original = 0;
    await page.route("**/api/v3/recovery/export", async (route) => {
      original += 1;
      await route.continue({ url: redirectResponse.url(status) });
    });
    await page.getByRole("button", { name: "Export recovery envelope", exact: true }).click();
    await page.getByRole("button", { name: "Confirm export", exact: true }).click();
    await expect(page.getByRole("alert")).toBeVisible();
    expect(original).toBe(1);
    expect(redirectResponse.requests()).toBe(1);
    expect(requests()).toBe(0);
    await expect(page.getByRole("alert")).toContainText(identity.operationId);
  });

  test(`detects a followed ${status} redirect at the same-origin destination [CC-WEB-001]`, async ({
    page,
    redirectResponse,
  }) => {
    await openAuthenticated(page);
    const requests = destination(page);
    await page.route("**/__synthetic/redirect-control", (route) =>
      route.continue({ url: redirectResponse.url(status) }),
    );
    await page.evaluate(async () => {
      try {
        await fetch("/__synthetic/redirect-control", { method: "POST", redirect: "follow" });
      } catch {
        // Delivery can refuse after redispatch; the destination counter still detects the effect.
      }
    });
    expect(redirectResponse.requests()).toBe(1);
    expect(requests()).toBe(1);
  });
}
