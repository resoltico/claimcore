import { expectCaseReadsSettled } from "./case-readiness";
import { randomUUID } from "node:crypto";
import { expect } from "@playwright/test";
import { test } from "./redirect-response";
import { openCase, startAct, reviewDraft } from "./case-workflow";
import type { ServiceResponse } from "../src/protocol/types";
import { openAuthenticated } from "./session-helpers";
for (const status of [301, 302, 303, 307, 308]) {
  test(`refuses ordinary API ${status} after original acceptance without redirect dispatch`, async ({
    page,
    redirectResponse,
  }) => {
    await openAuthenticated(page);
    await openCase(page, `REDIRECT-${randomUUID()}`);
    await startAct(page, "case.close", "en");
    await reviewDraft(page);
    await redirectResponse.arm("/api/operation.execute", status);
    await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
    await expect(
      page.getByRole("heading", {
        name: "We could not confirm whether this change was recorded. Send it again; it cannot be recorded twice.",
        exact: true,
      }),
    ).toBeVisible();
    const evidence = await redirectResponse.evidence();
    expect(evidence.originals).toBe(1);
    expect(evidence.destinations).toBe(0);
    const payload = evidence.reply;
    const { outcome } = payload as ServiceResponse<"operation.execute">;
    expect(outcome.kind === "ACCEPTED" || outcome.kind === "ALREADY_ACCEPTED").toBe(true);
    await expect(
      page.getByRole("button", {
        name: "Send again",
        exact: true,
      }),
    ).toBeVisible();
    await page
      .getByRole("button", {
        name: "Send again",
        exact: true,
      })
      .click();
    await expectCaseReadsSettled(page);
    await expect(page.locator("#case-detail-title")).toBeVisible();
    expect((await redirectResponse.evidence()).originals).toBe(1);
    expect((await redirectResponse.evidence()).destinations).toBe(0);
  });
  test(`detects a followed ${status} redirect at the same-origin destination`, async ({
    page,
    redirectResponse,
  }) => {
    await openAuthenticated(page);
    await redirectResponse.arm("/__synthetic/redirect-control", status);
    await page.evaluate(async () => {
      await fetch("/__synthetic/redirect-control", {
        method: "POST",
        redirect: "follow",
      });
    });
    const evidence = await redirectResponse.evidence();
    expect(evidence.originals).toBe(1);
    expect(evidence.destinations).toBe(1);
  });
}
