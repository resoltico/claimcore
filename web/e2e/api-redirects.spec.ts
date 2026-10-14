import { randomUUID } from "node:crypto";
import { expect } from "@playwright/test";
import { test } from "./redirect-response";
import { openCase, startCommand, prepare } from "./case-workflow";
import { isResponse } from "../src/generated/contracts/validation";
import type { ServiceResponse } from "../src/api/types";
import { openAuthenticated } from "./session-helpers";

for (const status of [301, 302, 303, 307, 308]) {
  test(`refuses ordinary API ${status} after original acceptance without redirect dispatch [CC-WEB-001]`, async ({
    page,
    redirectResponse,
  }) => {
    await openAuthenticated(page);
    await openCase(page, `REDIRECT-${randomUUID()}`);
    await startCommand(page, "Close the case");
    await prepare(page);
    await redirectResponse.arm("/api/command.execute", status);
    await page.getByText("I confirm these changes.", { exact: true }).click();
    await page.getByRole("button", { name: "Record changes" }).click();
    await expect(page.getByRole("alert")).toContainText("unknown");
    const evidence = await redirectResponse.evidence();
    expect(evidence.originals).toBe(1);
    expect(evidence.destinations).toBe(0);
    const payload = evidence.reply;
    expect(await isResponse("command.execute", payload)).toBe(true);
    const { outcome } = payload as ServiceResponse<"command.execute">;
    expect(outcome.tag === "ACCEPTED" || outcome.tag === "ALREADY_ACCEPTED").toBe(true);
    await expect(
      page.getByRole("button", { name: "Retry the same operation", exact: true }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Retry the same operation", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Recorded change" })).toBeVisible();
    expect((await redirectResponse.evidence()).originals).toBe(1);
    expect((await redirectResponse.evidence()).destinations).toBe(0);
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
