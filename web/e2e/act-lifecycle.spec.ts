import { expectCaseRevision, workRequest } from "./work-workflow";
import { goToView, showOperationLookup } from "./workspace-navigation";
import { expectCaseReadsSettled } from "./case-readiness";
import { randomUUID } from "node:crypto";

import { expect, test, type Page } from "./fixtures";

import {
  completeAct,
  openCase,
  lookupCase,
  caseActionButton,
  startClose,
  reviewDraft,
  recordReviewed,
} from "./case-workflow";
import { applyPeople } from "./people-setup";
import { actPath, casePath } from "../src/routing/routes";
import { operationId } from "../src/operation/operationId";
import { confirmReview } from "./localization-support";
import { expectAccessible, openAuthenticated } from "./session-helpers";

const expectRevision = async (page: Page, revision: string) => {
  await expectCaseRevision(page, Number(revision));
};
const reference = (): string => `BROWSER-${randomUUID().slice(0, 8)}`;

const ambiguousRowLinks = (
  path: string,
  kind: string,
  key: string,
  first: string,
  second: string,
): string[] => {
  const legacy = new URLSearchParams({
    binding: `{"kind":"${kind}","${key}":"${first}","${key}":"${second}"}`,
  });
  const repeated = new URLSearchParams([
    [key, first],
    [key, second],
  ]);
  return [`${path}?${legacy}`, `${path}?${repeated}`];
};

const expectRejectedRowLinks = async (page: Page, caseId: string, paths: ReadonlyArray<string>) => {
  const writes: string[] = [];
  const observe = (request: { url: () => string }) => {
    const path = new URL(request.url()).pathname;
    if (path === "/api/operation.preview" || path === "/api/operation.execute") {
      writes.push(path);
    }
  };
  page.on("request", observe);
  try {
    for (const path of paths) {
      await page.goto(path);
      await expect(
        page.getByRole("alert").filter({ hasText: "This action is not currently offered" }),
      ).toBeVisible();
      await expect(page.getByRole("dialog")).toHaveCount(0);
      await page.getByRole("button", { name: "Return to case" }).click();
      await expect(page).toHaveURL((url) => url.pathname === casePath(caseId) && url.search === "");
      expect(writes).toEqual([]);
    }
  } finally {
    page.off("request", observe);
  }
};

const decide = async (page: Page): Promise<void> => {
  await completeAct(page, "case.decide", {
    "Payment decision date": "2026-09-03",
    "Payable amount": "300.25",
    "Payable currency": "EUR",
  });
};

test("publishes open, amendment, decision, withdrawal, full history and operation lookup", async ({
  page,
}) => {
  const caseReference = reference();
  await openAuthenticated(page);
  const opened = await openCase(page, caseReference);
  await completeAct(page, "case.amendRegistration", {
    "Policy number": "Synthetic amended claimant",
  });
  await decide(page);
  await completeAct(page, "case.withdrawDecision");
  await expectRevision(page, "4");
  await expect(page.locator(".case-timeline-panel .region-timeline > li")).toHaveCount(4);
  const opening = page.locator(
    `.case-timeline-panel .region-timeline > li[data-effect$=".${opened.operationId}"]`,
  );
  await expect(opening).toHaveCount(1);
  await expect(opening).toContainText("Case facts recorded");
  await opening.getByRole("button", { name: "View details", exact: true }).click();
  const details = page.getByRole("dialog", { name: "View details", exact: true });
  await expect(details).toContainText(opened.operationId);
  await details.getByRole("button", { name: "Close", exact: true }).first().click();
  await expect(await caseActionButton(page, "case.close", "en")).toBeVisible();
  await page.keyboard.press("Escape");
  await expectAccessible(page);

  await goToView(page, "recovery");
  await (await showOperationLookup(page)).fill(opened.operationId);
  await page.getByRole("button", { name: "Look up recorded result" }).click();
  await page.getByRole("button", { name: "Technical details" }).click();
  const technical = page.getByRole("dialog", { name: "Technical details" });
  await expect(technical.locator('[data-entry="operationId"]')).toContainText(opened.operationId);
  await expectAccessible(page);
  await technical.getByRole("button", { name: "Close", exact: true }).first().click();

  await goToView(page, "cases");
  await lookupCase(page, opened.caseReference);
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await expect(page.getByText("Synthetic amended claimant").first()).toBeVisible();
});

test("publishes payment, correction, close and reopen with current-case parity", async ({
  page,
}) => {
  const caseReference = reference();
  await openAuthenticated(page);
  const opened = await openCase(page, caseReference);
  await decide(page);
  const payment = await completeAct(page, "case.recordPayment", {
    "Payment date": "2026-09-05",
    "Paid amount": "300.25",
    "Payment currency": "EUR",
  });
  const target = { kind: "ID", caseId: opened.operationId } as const;
  await page.goto(actPath(target, "case.removePayment"));
  const chooser = page.getByRole("dialog");
  await expect(chooser.getByText("Choose the payment entry to remove.")).toBeVisible();
  await chooser
    .getByRole("row")
    .filter({ hasText: "300.25 EUR" })
    .getByRole("button", {
      name: "Remove payment entry",
    })
    .click();
  await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expectCaseReadsSettled(page);
  await startClose(page);
  await reviewDraft(page);
  await recordReviewed(page);
  await expect(page.getByText("Closed", { exact: true }).first()).toBeVisible();
  await completeAct(page, "case.reopen", { Reason: "Review additional evidence" });
  await expectRevision(page, "6");
  const entries = page.locator(".case-timeline-panel .region-timeline > li");
  await expect(entries).toHaveCount(8);
  let caseReferences = 0;
  let paymentReferences = 0;
  for (const entry of await entries.all()) {
    await entry.getByRole("button", { name: "View details", exact: true }).click();
    const details = page.getByRole("dialog", { name: "View details", exact: true });
    caseReferences += await details.locator(`[data-entry="${opened.operationId}.id"]`).count();
    paymentReferences += await details.locator(`[data-entry="${payment.operationId}.id"]`).count();
    await details.getByRole("button", { name: "Close", exact: true }).first().click();
  }
  expect(caseReferences).toBe(6);
  expect(paymentReferences).toBe(2);
  await expectAccessible(page);
});

test("adds, edits after reload, and removes a selected party in the published workspace", async ({
  page,
}) => {
  await openAuthenticated(page);
  const opened = await openCase(page, reference());
  const target = { kind: "ID", caseId: opened.operationId } as const;
  const parties = page.locator('.case-detail [data-region="PARTIES"]');
  const first = "Synthetic lifecycle party";
  const updated = "Synthetic updated party";

  await parties.getByRole("button", { name: "Add party", exact: true }).click();
  const add = page.getByRole("dialog", { name: "Add party", exact: true });
  await expect(add).toBeVisible();
  await add.getByRole("radio", { name: "Claimant", exact: true }).check();
  await reviewDraft(page, { "Party name": first });
  await confirmReview(page);
  await recordReviewed(page);

  const addParty = (caseId: string, partyId: string, name: string) =>
    applyPeople(page, {
      operationId: partyId,
      task: "party.add",
      binding: { kind: "PARTY", target: { kind: "ID", caseId } },
      inputs: { "input:partyRole": "CLAIMANT", "input:partyName": name },
    });
  const otherCase = operationId();
  await applyPeople(page, {
    operationId: otherCase,
    task: "case.open",
    binding: { kind: "CASE", expectedRevision: 0 },
    inputs: {},
  });
  const foreignParty = operationId();
  await addParty(otherCase, foreignParty, "Synthetic foreign party");
  const second = "Synthetic second party";
  const secondId = operationId();
  await addParty(opened.operationId, secondId, second);
  await page.goto(casePath(opened.operationId));
  await expect(parties.getByRole("row").filter({ hasText: second })).toBeVisible();

  const read = await workRequest(page, "case.get", { target });
  if (read.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected current parties.");
  }
  const selected = read.outcome.data.parties.find(({ content }) => content.name === first);
  if (selected === undefined) {
    throw new Error("Expected the first synthetic party.");
  }

  const removal = actPath(target, "party.remove");
  await expectRejectedRowLinks(page, target.caseId, [
    removal,
    `${removal}?partyId=`,
    `${removal}?partyId=not-an-id`,
    `${removal}?${new URLSearchParams({ binding: '{"kind":"PARTY"}' })}`,
    ...ambiguousRowLinks(removal, "PARTY", "partyId", selected.partyId, secondId),
    actPath(target, "party.remove", { kind: "PARTY", partyId: foreignParty }),
  ]);

  const chooseRowAction = async (name: string, action: string) => {
    await parties
      .getByRole("row")
      .filter({ hasText: name })
      .locator("button[aria-haspopup]")
      .click();
    await page.getByRole("menuitem", { name: action, exact: true }).click();
  };
  await expect(parties.getByRole("row").filter({ hasText: first })).toBeVisible();
  await chooseRowAction(first, "Update party");
  await expect(page.getByRole("dialog", { name: "Update party", exact: true })).toBeVisible();
  await page.reload();
  const edit = page.getByRole("dialog", { name: "Update party", exact: true });
  await expect(edit.getByRole("button", { name: "Review changes" })).toBeVisible();
  await expect(edit.getByRole("combobox", { name: "Access change" })).toHaveCount(0);
  const previewRequest = page.waitForRequest(
    (request) => new URL(request.url()).pathname === "/api/operation.preview",
  );
  await expect(edit.locator("button[data-confirm-operation]")).toHaveCount(0);
  await reviewDraft(page, { "Party name": updated });
  expect((await previewRequest).postDataJSON()).toMatchObject({
    binding: { partyId: selected.partyId },
  });
  await confirmReview(page);
  await recordReviewed(page);

  await expect(parties.getByRole("row").filter({ hasText: updated })).toBeVisible();
  await chooseRowAction(updated, "Remove party");
  await expect(page.getByRole("dialog").locator("button[data-confirm-operation]")).toBeEnabled();
  await expect(parties.getByRole("row").filter({ hasText: updated })).toHaveCount(1);
  await recordReviewed(page);
  await expect(parties.getByRole("row").filter({ hasText: updated })).toHaveCount(0);
  await expectRejectedRowLinks(page, target.caseId, [
    actPath(target, "party.remove", { kind: "PARTY", partyId: selected.partyId }),
  ]);
  await expect(parties.getByRole("row").filter({ hasText: second })).toBeVisible();
});

test("rejects ambiguous payment links and keeps the chosen removal across reload", async ({
  page,
}) => {
  await openAuthenticated(page);
  const opened = await openCase(page, reference());
  const target = { kind: "ID", caseId: opened.operationId } as const;
  await decide(page);
  const payments = [];
  for (const amount of ["100", "50"] as const) {
    payments.push(
      await completeAct(page, "case.recordPayment", {
        "Payment date": "2026-09-05",
        "Paid amount": amount,
        "Payment currency": "EUR",
      }),
    );
  }

  const removal = actPath(target, "case.removePayment");
  await expectRejectedRowLinks(
    page,
    target.caseId,
    ambiguousRowLinks(
      removal,
      "CASE",
      "paymentId",
      payments[0]!.operationId,
      payments[1]!.operationId,
    ),
  );

  const paymentId = payments[1]!.operationId;
  const path = actPath(target, "case.removePayment", { kind: "CASE", paymentId });
  const dialog = page.getByRole("dialog");
  let previews = 0;
  let writes = 0;
  const observe = (request: { url: () => string }) => {
    const endpoint = new URL(request.url()).pathname;
    if (endpoint === "/api/operation.preview") {
      previews += 1;
    } else if (endpoint === "/api/operation.execute") {
      writes += 1;
    }
  };
  page.on("request", observe);

  const expectReview = async (navigate: () => Promise<unknown>, expectedPreviews: number) => {
    const request = page.waitForRequest(
      (value) => new URL(value.url()).pathname === "/api/operation.preview",
    );
    const [submitted] = await Promise.all([request, navigate()]);
    expect(submitted.postDataJSON()).toMatchObject({ binding: { paymentId } });
    await expect(page).toHaveURL((url) => url.pathname + url.search === path);
    await expect(dialog.locator("button[data-confirm-operation]")).toBeEnabled();
    expect(previews).toBe(expectedPreviews);
    expect(writes).toBe(0);
    return submitted.postDataJSON() as { operationId: string };
  };

  await page.goto(removal);
  await expect(dialog.getByText("Choose the payment entry to remove.")).toBeVisible();
  expect(previews).toBe(0);
  const first = await expectReview(
    () =>
      dialog
        .getByRole("row")
        .filter({ hasText: "50.00 EUR" })
        .getByRole("button", { name: "Remove payment entry", exact: true })
        .click(),
    1,
  );
  const reloaded = await expectReview(() => page.reload(), 2);
  expect(reloaded.operationId).not.toBe(first.operationId);
  await dialog.getByRole("button", { name: "Close", exact: true }).first().click();
  await page
    .getByRole("dialog", { name: "Discard these draft changes?" })
    .getByRole("button", { name: "Discard and leave", exact: true })
    .click();
  await expect(page).toHaveURL(
    (url) => url.pathname === casePath(target.caseId) && url.search === "",
  );
  expect(previews).toBe(2);
  expect(writes).toBe(0);
  page.off("request", observe);
  await expectRevision(page, "4");

  await applyPeople(page, {
    operationId: operationId(),
    task: "case.removePayment",
    binding: { kind: "CASE", target, paymentId, expectedRevision: 4 },
    inputs: {},
  });
  await expectRejectedRowLinks(page, target.caseId, [path]);
});
