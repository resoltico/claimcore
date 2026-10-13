import { randomUUID } from "node:crypto";
import { expect } from "@playwright/test";
import { test } from "./redirect-response";
import { openCase, startCommand, prepare, keepForRecovery } from "./case-workflow";
import { isWebV3Response } from "../src/generated/contracts/web-v3.validation";
import type { WebV3Response } from "../src/generated/contracts/web-v3.types";
import { openAuthenticated } from "./session-helpers";

for (const status of [301, 302, 303, 307, 308]) {
  test(`refuses ordinary API ${status} after original acceptance without redirect dispatch [CC-WEB-001]`, async ({
    page,
    redirectResponse,
  }) => {
    await openAuthenticated(page);
    await openCase(page, `REDIRECT-${randomUUID()}`);
    await startCommand(page, "Close the case");
    const identity = await prepare(page);
    await redirectResponse.arm("/api/v3/operations/submit", status);
    await page.getByText("I confirm these changes.", { exact: true }).click();
    await page.getByRole("button", { name: "Record changes" }).click();
    await expect(page.getByRole("alert")).toContainText("Recovery");
    const evidence = await redirectResponse.evidence();
    expect(evidence.originals).toBe(1);
    expect(evidence.destinations).toBe(0);
    const payload = evidence.reply;
    expect(await isWebV3Response("command.execute", payload)).toBe(true);
    const { outcome } = payload as WebV3Response<"command.execute">;
    expect(outcome.tag === "COMPLETED" && outcome.data.execution.tag === "ACCEPTED").toBe(true);
    await expect(page.getByRole("heading", { name: "Recovery", exact: true })).toBeVisible();
    await expect(page.getByRole("alert")).toContainText(identity.operationId);
    await expect(page.getByRole("alert")).toContainText(identity.requestSha256);
    await page.getByRole("button", { name: "Operations", exact: true }).click();
    await expect(page.getByLabel("Exact operation ID")).toHaveValue(identity.operationId);
    await page.getByRole("button", { name: "Look up recorded result" }).click();
    await expect(
      page.getByText(`Accepted operation ${identity.operationId}`, { exact: false }),
    ).toBeVisible();
    expect((await redirectResponse.evidence()).originals).toBe(1);
    expect((await redirectResponse.evidence()).destinations).toBe(0);
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
    await redirectResponse.arm("/api/v3/recovery/export", status);
    await page.getByRole("button", { name: "Export recovery envelope", exact: true }).click();
    await page.getByRole("button", { name: "Confirm export", exact: true }).click();
    await expect(page.getByRole("alert")).toBeVisible();
    const evidence = await redirectResponse.evidence();
    expect(evidence.originals).toBe(1);
    expect(evidence.destinations).toBe(0);
    await expect(page.getByRole("alert")).toContainText(identity.operationId);
  });

  test(`detects a followed ${status} redirect at the same-origin destination [CC-WEB-001]`, async ({
    page,
    redirectResponse,
  }) => {
    await openAuthenticated(page);
    await redirectResponse.arm("/__synthetic/redirect-control", status);
    await page.evaluate(async () => {
      await fetch("/__synthetic/redirect-control", { method: "POST", redirect: "follow" });
    });
    const evidence = await redirectResponse.evidence();
    expect(evidence.originals).toBe(1);
    expect(evidence.destinations).toBe(1);
  });
}
