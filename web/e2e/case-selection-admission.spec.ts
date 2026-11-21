import { expect, test, type Request } from "./fixtures";
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
  await page.locator(".case-selection-repair input").fill("2026-02-02");
  await page.getByRole("button", { name: "تطبيق التصحيح" }).click();
  await expect(page).toHaveURL(/receivedThrough=2026-02-02$/u);
  await expect(title).toBeFocused();
  const requests = [await selecting];
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
      body: JSON.stringify({ ...selection, limit: 10 }),
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

test("keeps a paused search draft and commits it on explicit submission", async ({ page }) => {
  await openAuthenticated(page);
  await selectLanguage(page, "en");
  const original = "/cases?view=ALL&status=OPENED";
  await page.goto(original, { waitUntil: "networkidle" });
  const search = page.getByRole("searchbox", { name: /Filter this view/u });
  await expect(search).toBeVisible();
  await page.clock.install();
  const reads: string[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (paths.has(path)) {
      reads.push(path);
    }
  });

  await search.fill("North ");
  await page.clock.runFor(700);
  await expect(page).toHaveURL((url) => url.pathname + url.search === original);
  await expect(search).toBeFocused();
  await expect(search).toHaveValue("North ");
  const correction = page.getByText("Remove spaces at the start or end.", { exact: true });
  await expect(correction).toHaveCount(0);
  expect(reads).toEqual([]);

  await search.press("Enter");
  await expect(correction).toBeVisible();
  await expect(search).toBeFocused();
  await expect(search).toHaveAttribute("aria-invalid", "true");
  expect(reads).toEqual([]);

  const submitted = page.waitForRequest(
    (request) => new URL(request.url()).pathname === caseListPath,
  );
  await search.pressSequentially("Star");
  await page.clock.runFor(700);
  expect(reads).toEqual([]);
  await search.press("Enter");
  for (const request of [await submitted]) {
    expect(request.postDataJSON()).toMatchObject({
      view: "ALL",
      status: ["OPENED"],
      text: "North Star",
    });
  }
  await expect(search).toBeFocused();
  await expect(search).toHaveValue("North Star");
  await expect(correction).toHaveCount(0);

  const beforeBookmark = reads.length;
  await page.goto(`${original}&q=North+`, { waitUntil: "networkidle" });
  await expect(
    page.getByText("This Cases selection cannot be used.", { exact: true }),
  ).toBeVisible();
  await expect(page.locator("#case-list-title")).toBeFocused();
  expect(reads.slice(beforeBookmark)).toEqual([]);
});

test("repairs exactly one duplicate scalar and restores the original choices on Back", async ({
  page,
}) => {
  await openAuthenticated(page);
  await selectLanguage(page, "en");
  const original = "/cases?view=ALL&status=OPENED&receivedFrom=2026-09-02&receivedFrom=2026-09-04";
  await page.goto(original, { waitUntil: "networkidle" });
  const earlier = page.getByRole("radio", { name: "2026-09-02" });
  const later = page.getByRole("radio", { name: "2026-09-04" });
  const invalid = page.getByText("This Cases selection cannot be used.", { exact: true });
  const apply = page.getByRole("button", { name: "Apply correction" });
  await expect(invalid).toBeVisible();
  await expect(earlier).not.toBeChecked();
  await expect(later).not.toBeChecked();
  await expect(apply).toBeDisabled();

  await later.focus();
  await later.press("Space");
  await expect(later).toBeChecked();
  const choice = page.getByRole("textbox", { name: "Received from" });
  await expect(choice).toHaveValue("2026-09-04");
  await choice.fill("");
  await expect(apply).toBeDisabled();

  const selected = page.waitForRequest(
    (request) => new URL(request.url()).pathname === caseListPath,
  );
  await choice.fill("2026-09-03");
  await apply.click();
  await expect(page).toHaveURL(/receivedFrom=2026-09-03$/u);
  expect((await selected).postDataJSON()).toMatchObject({
    view: "ALL",
    status: ["OPENED"],
    receivedFrom: "2026-09-03",
  });
  expect(new URL(page.url()).searchParams.getAll("receivedFrom")).toEqual(["2026-09-03"]);

  await page.goBack({ waitUntil: "commit" });
  await expect(page).toHaveURL(new URL(original, page.url()).href);
  await expect(invalid).toBeVisible();
  await expect(page.locator("#case-list-title")).toBeFocused();
  await expect(earlier).not.toBeChecked();
  await expect(later).not.toBeChecked();
  await expect(apply).toBeDisabled();
});
