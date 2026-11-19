import { expect, test, type Request } from "@playwright/test";
import { csvExportTransport, endpoints } from "@contracts/client";
import { selectLanguage } from "./localization-support";
import { browserRequest, installationId, openAuthenticated, sessionToken } from "./session-helpers";
import { settledWorkspaceHeader } from "./workspace-navigation";

const caseListPath = endpoints.find((endpoint) => endpoint.id === "case.list")!.path;
const summaryPath = endpoints.find((endpoint) => endpoint.id === "case.summary")!.path;
const exportPath = csvExportTransport.path;
const paths = new Set<string>([caseListPath, summaryPath, exportPath]);

test("admits a complete Cases URL before dispatch and reconstructs invalid and repaired routes", async ({
  page,
}) => {
  test.setTimeout(60_000);
  await page.setViewportSize({ width: 375, height: 760 });
  await openAuthenticated(page);
  await selectLanguage(page, "ar");

  const selections: string[] = [];
  const observe = (request: Request) => {
    const path = new URL(request.url()).pathname;
    if (paths.has(path)) {
      selections.push(path);
    }
  };
  page.on("request", observe);

  const title = page.locator("#case-list-title");
  const invalid = page.getByText("لا يمكن استخدام تحديد المطالبات هذا.", { exact: true });

  await page.goto("/cases?view=ALL&q=%C0%AF", { waitUntil: "networkidle" });
  await settledWorkspaceHeader(page);
  await expect(invalid).toBeVisible();
  await expect(title).toBeFocused();
  await expect(page.locator(".case-selection-repair input")).toHaveValue("%C0%AF");
  expect(selections).toEqual([]);

  const raw =
    "/cases?view=ALL&status=OPENED&status=CLOSED&q=%D8%B9%2B&receivedFrom=2026-02-02&receivedThrough=2026-02-01";
  await page.goto(raw, { waitUntil: "networkidle" });
  await settledWorkspaceHeader(page);
  await expect(invalid).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(title).toBeFocused();
  await expect(page.locator(".case-selection-repair p")).toContainText("تاريخ الاستلام حتى");
  await expect(page.locator(".case-selection-repair input")).toHaveValue("2026-02-01");
  await expect(page.locator(".worklist-row,.summary-band")).toHaveCount(0);
  expect(selections).toEqual([]);

  const selecting = page.waitForRequest(
    (request) => new URL(request.url()).pathname === caseListPath,
  );
  const summarizing = page.waitForRequest(
    (request) => new URL(request.url()).pathname === summaryPath,
  );
  await page.locator(".case-selection-repair input").fill("2026-02-02");
  await page.getByRole("button", { name: "تطبيق التصحيح" }).click();
  await expect(page).toHaveURL(/receivedThrough=2026-02-02$/u);
  await expect(title).toBeFocused();
  const requests = await Promise.all([selecting, summarizing]);
  const selection = {
    view: "ALL",
    status: ["OPENED", "CLOSED"],
    text: "ع+",
    receivedFrom: "2026-02-02",
    receivedThrough: "2026-02-02",
  };
  for (const request of requests) {
    expect(request.postDataJSON()).toMatchObject(selection);
  }
  await expect(page.locator('.worklist[aria-busy="false"]')).toBeVisible();

  const beforeBack = selections.length;
  await page.goBack({ waitUntil: "commit" });
  await expect(page).toHaveURL(/receivedThrough=2026-02-01$/u);
  await expect(invalid).toBeVisible();
  await expect(title).toBeFocused();
  await expect(page.locator(".worklist-row,.summary-band")).toHaveCount(0);
  expect(selections.slice(beforeBack)).toEqual([]);

  await page.goForward({ waitUntil: "commit" });
  await expect(page).toHaveURL(/receivedThrough=2026-02-02$/u);
  await expect(title).toBeFocused();
  await expect(invalid).toHaveCount(0);
});

test("reversed ranges use the same declared diagnostic on Cases reads and both CSV routes", async ({
  page,
}) => {
  await openAuthenticated(page);
  const token = await sessionToken(page);
  const selection = { receivedFrom: "2026-02-02", receivedThrough: "2026-02-01" };
  const headers = {
    "Content-Type": "application/json",
    "X-ClaimCore-Antiforgery": token,
  };
  for (const path of [caseListPath, summaryPath]) {
    const reply = await browserRequest(page, path, {
      method: "POST",
      headers,
      body: JSON.stringify(path === caseListPath ? { ...selection, limit: 10 } : selection),
    });
    expect(reply.status).toBe(200);
    expect(reply.payload).toMatchObject({
      outcome: {
        kind: "REJECTED",
        rejection: { code: "CASE_DATE_RANGE", field: "receivedThrough", action: "CORRECT_INPUT" },
      },
    });
  }
  for (const query of ["case.list", "case.summary"]) {
    const reply = await browserRequest(page, exportPath, {
      method: "POST",
      headers: { ...headers, "ClaimCore-Installation": await installationId(page, token) },
      body: JSON.stringify({ query, selection }),
    });
    expect(reply.status).toBe(200);
    expect(reply.payload).toMatchObject({
      outcome: {
        kind: "REJECTED",
        rejection: {
          kind: "REFUSED",
          advice: {
            action: "CORRECT_INPUT",
            message: {
              messageId: "diagnostic.CASE_DATE_RANGE",
              arguments: [
                {
                  key: "field",
                  value: { kind: "MESSAGE", messageId: "input.receivedThrough.label" },
                },
              ],
            },
          },
        },
      },
    });
  }
});
