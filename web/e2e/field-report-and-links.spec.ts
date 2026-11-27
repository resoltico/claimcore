import { workspaceSettingsNavigation } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "./fixtures";
import {
  type RequestByEndpoint,
  type ResponseFor,
  consumerContractIdentity,
} from "@contracts/client";
import {
  installationId,
  browserRequest,
  expectAccessible,
  openAuthenticated,
  sessionToken,
} from "./session-helpers";
import { workRequest } from "./work-workflow";
import { operationId } from "../src/operation/operationId";
import { referencePath } from "../src/routing/routes";
import { selectedActor, syntheticPrincipals } from "./people-setup";
import { reviewedPeopleBinding } from "./people-revision";
import { selectLanguage } from "./localization-support";
import { noHorizontalOverflow } from "./layout-support";

const owner = async (page: Page) => {
  await openAuthenticated(page);
  const token = await sessionToken(page);
  return { token, installationId: await installationId(page, token) };
};
type Access = Awaited<ReturnType<typeof owner>>;

const target = (caseReference: string) => ({ kind: "REFERENCE" as const, caseReference });

const execute = async (
  page: Page,
  access: Access,
  body: RequestByEndpoint["operation.execute"],
) => {
  const result = await workRequest(page, "operation.execute", body, access);
  expect(result.outcome.kind).toBe("ACCEPTED");
};

const human = async <K extends "case.get" | "case.summary" | "operation.preview">(
  page: Page,
  access: Access,
  endpoint: K,
  body: RequestByEndpoint[K],
) => {
  const reply = await browserRequest(page, `/api/${endpoint}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": access.token,
      "ClaimCore-Installation": access.installationId,
      "ClaimCore-Representation": "HUMAN",
    },
    body: JSON.stringify(body),
  });
  expect(reply.status).toBe(200);
  return reply.payload as ResponseFor<K, "HUMAN">;
};

test("renders both related directions and requires a distinct Owner for Owner rebinding", async ({
  page,
}) => {
  test.setTimeout(120_000);
  const access = await owner(page);
  const prefix = `LINKFIELD-${randomUUID().slice(0, 8)}`;
  const a = `${prefix}-A`;
  const b = `${prefix}-B`;
  const c = `${prefix}-C`;
  for (const reference of [a, b, c]) {
    await execute(page, access, {
      operationId: operationId(),
      task: "case.open",
      binding: { kind: "CASE", expectedRevision: 0 },
      inputs: { "field:caseReference": reference },
    });
  }

  const add = (from: string, to: string) =>
    execute(page, access, {
      operationId: operationId(),
      task: "link.add",
      binding: { kind: "LINK", target: target(from) },
      inputs: { "input:relatedCaseReference": to },
    });
  const related = async (reference: string) => {
    const reply = await human(page, access, "case.get", { target: target(reference) });
    if (reply.outcome.kind !== "SUCCEEDED") {
      throw new Error("A readable case is required");
    }
    const regions = reply.outcome.document.screen?.regions.filter(
      (item) => item.role === "RELATED_CASES",
    );
    expect(regions).toHaveLength(1);
    const region = regions?.[0];
    if (region?.content.kind !== "TABLE") {
      throw new Error("Related cases require the supplied table");
    }
    return { table: region.content, actions: region.actions };
  };

  await add(a, b);
  expect((await related(a)).table.groups?.map((group) => group.titleId)).toEqual([
    "section.relatedCases.outgoing",
  ]);
  const incoming = await related(b);
  expect(incoming.table.groups?.map((group) => group.titleId)).toEqual([
    "section.relatedCases.incoming",
  ]);
  expect(incoming.actions?.map((act) => act.task)).toContain("link.add");
  await add(b, c);
  const both = await related(b);
  expect(both.table.groups?.map((group) => [group.titleId, group.count])).toEqual([
    ["section.relatedCases.outgoing", 1],
    ["section.relatedCases.incoming", 1],
  ]);
  await page.goto(referencePath(b));
  const region = page.locator('[data-region="RELATED_CASES"]');
  await expect(region).toContainText(a);
  await expect(region).toContainText(c);
  await expect(region.getByRole("columnheader", { name: "Case reference" })).toBeVisible();
  await expectAccessible(page);

  const people = await syntheticPrincipals();
  for (const [subject, self] of [
    [people.ownerSubject, true],
    [people.stewardSubject, false],
  ] as const) {
    const actor = await selectedActor(page, { kind: "HUMAN", issuer: people.issuer, subject });
    const binding = await reviewedPeopleBinding(page, actor);
    const preview = await human(page, access, "operation.preview", {
      operationId: operationId(),
      task: "people.changeSignInIdentity",
      binding,
      inputs: {
        "input:principal.issuer": people.issuer,
        "input:principal.subject": `preview-${randomUUID()}`,
      },
    });
    if (self) {
      expect(preview.outcome).toMatchObject({
        kind: "REJECTED",
        rejection: { advice: { message: { messageId: "diagnostic.DISTINCT_OWNER_REQUIRED" } } },
      });
      expect(await reviewedPeopleBinding(page, actor)).toEqual(binding);
      continue;
    }
    if (preview.outcome.kind !== "REVIEWED") {
      throw new Error(`Expected a non-Owner rebind preview, received ${preview.outcome.kind}`);
    }
    const notices = preview.outcome.review.document.screen?.regions.filter(
      (item) => item.role === "NOTICE",
    );
    expect(notices).toHaveLength(1);
    expect(notices?.[0]?.content).toEqual({
      kind: "NOTICE",
      notices: [{ messageId: "task.people.changeSignInIdentity.meaning", tone: "INFO" }],
    });
    expect(await reviewedPeopleBinding(page, actor)).toEqual(binding);
  }
  await page.setViewportSize({ width: 320, height: 900 });
  await selectLanguage(page, "ar");
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(region).toContainText(c);
  await noHorizontalOverflow(page, "narrow");
});

const assertReportTail = async (
  page: Page,
  access: Access,
  request: RequestByEndpoint["case.summary"],
) => {
  const continued = await human(page, access, "case.summary", request);
  if (continued.outcome.kind !== "SUCCEEDED") {
    throw new Error("HUMAN admits the remaining row");
  }
  const results = continued.outcome.document.screen?.regions.filter(
    (item) => item.role === "RESULTS",
  );
  expect(results).toHaveLength(1);
  const table = results?.[0]?.content;
  if (table?.kind !== "TABLE") {
    throw new Error("Expected the remaining report row");
  }
  expect(table.rows).toHaveLength(1);
  expect(table.rows[0]?.values[0]).toEqual({ kind: "IDENTIFIER", value: "2022-03" });
  expect(continued.outcome.document.nextCursor).toBeNull();
};

const summaryRoute = "**/api/case.summary";
const nextSummaryRequest = (page: Page) =>
  page.waitForRequest((request) => new URL(request.url()).pathname === "/api/case.summary");

for (const releaseOn of ["Settings", "return"] as const) {
  test(`retires a real report reply released on ${releaseOn} @engine`, async ({ page }) => {
    await owner(page);
    const reference = `REPORT-DET${randomUUID().slice(0, 8)}`;
    const writer = await page.context().newPage();
    try {
      await writer.goto("/cases");
      const token = await sessionToken(writer);
      const access = { token, installationId: await installationId(writer, token) };
      const held = Promise.withResolvers<void>();
      const release = Promise.withResolvers<void>();
      const delivered = Promise.withResolvers<void>();
      let requests = 0;
      await page.route(summaryRoute, async (route) => {
        if (++requests > 1) {
          await route.continue();
          return;
        }
        const response = await route.fetch();
        expect(response.status()).toBe(200);
        const reply = (await response.json()) as ResponseFor<"case.summary", "HUMAN">;
        expect(reply.outcome).toMatchObject({
          kind: "SUCCEEDED",
          document: {
            screen: {
              regions: expect.arrayContaining([
                expect.objectContaining({
                  role: "TOTALS",
                  content: expect.objectContaining({
                    entries: [expect.objectContaining({ value: { kind: "COUNT", value: 0 } })],
                  }),
                }),
              ]),
            },
          },
        });
        held.resolve();
        await release.promise;
        await route.fulfill({ response });
        delivered.resolve();
      });
      const path = `/cases/report?q=${reference}`;
      try {
        await page.goto(path);
        await held.promise;
        await (await workspaceSettingsNavigation(page)).click();
        await expect(page.getByRole("heading", { name: "Settings", exact: true })).toBeVisible();
        await execute(writer, access, {
          operationId: operationId(),
          task: "case.open",
          binding: { kind: "CASE", expectedRevision: 0 },
          inputs: { "field:caseReference": reference },
        });
        const total = page.locator('[data-region="TOTALS"] [data-entry="total"] dd');
        if (releaseOn === "Settings") {
          release.resolve();
          await delivered.promise;
          await expect(total).toHaveCount(0);
        }
        await page.locator("[data-return-workspace]").click();
        await expect(page).toHaveURL(new RegExp(`${reference}$`, "u"));
        await expect(total).toHaveText("1");
        if (releaseOn === "return") {
          release.resolve();
          await delivered.promise;
          await expect(total).toHaveText("1");
        }
        expect(requests).toBe(2);
      } finally {
        release.resolve();
      }
    } finally {
      await writer.close();
    }
  });
}

test("the report's Cases link preserves an unqualified report's All selection @engine", async ({
  page,
}) => {
  await owner(page);
  await page.goto("/cases/report");
  const report = page.getByRole("region", { name: "Case report" });
  const cases = report.getByRole("link", { name: "Cases", exact: true });
  await expect(cases).toHaveAttribute("href", "/cases?view=ALL");
  await cases.press("Enter");
  await expect(page).toHaveURL(/\/cases\?view=ALL$/u);
  await page.goto("/cases");
  await expect(page).toHaveURL(/\/cases\?view=NEEDS_ATTENTION$/u);
});

const verifyReportDisclosure = async (page: Page) => {
  const report = page.getByRole("region", { name: "Case report" });
  const download = report.getByRole("button", { name: "Export report CSV" });
  const alert = report.getByRole("alert");
  const retry = alert.getByRole("button", { name: "Try again" });
  let attempts = 0;
  let release: () => void = () => undefined;
  const hold = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route(summaryRoute, async (route) => {
    attempts += 1;
    if (attempts === 1) {
      await route.fulfill({
        status: 200,
        json: {
          representation: "HUMAN",
          outcome: {
            kind: "REJECTED",
            rejection: {
              kind: "REFUSED",
              advice: {
                message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
                action: "ASK_FOR_ACCESS",
              },
            },
          },
        },
      });
    } else if (attempts === 2) {
      await hold;
      await route.fulfill({
        status: 429,
        json: { hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } },
      });
    } else {
      await route.continue();
    }
  });
  const [denied] = await Promise.all([
    nextSummaryRequest(page),
    report.getByRole("button", { name: "Next report page" }).click(),
  ]);
  const position = denied.postDataJSON() as RequestByEndpoint["case.summary"];
  expect(position.cursor).toEqual(expect.any(String));
  await expect(retry).toBeVisible();
  await expect(download).toBeDisabled();

  try {
    const [retried] = await Promise.all([nextSummaryRequest(page), retry.click()]);
    expect(retried.postDataJSON()).toEqual(position);
    await expect(report.getByRole("status")).toHaveAttribute("aria-busy", "true");
    await expect(download).toBeDisabled();
  } finally {
    release();
  }
  await expect(retry).toBeVisible();
  await expect(download).toBeDisabled();
  const [revalidated] = await Promise.all([nextSummaryRequest(page), retry.click()]);
  expect(revalidated.postDataJSON()).toEqual(position);
  await expect(report.locator('[aria-live="polite"]')).toHaveText("Report page 2");
  await expect(download).toBeEnabled();
  await page.unroute(summaryRoute);

  await page.route(summaryRoute, (route) =>
    route.fulfill({
      status: 401,
      json: { hostFailure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" } },
    }),
  );
  await report.getByRole("button", { name: "Restart report" }).click();
  await expect(retry).toBeVisible();
  await expect(download).toBeDisabled();
  await page.unroute(summaryRoute);
  await retry.click();
  await expect(report.getByRole("button", { name: "Next report page" })).toBeVisible();
  await expect(download).toBeEnabled();
};

const finishBrowserReport = async (page: Page, prefix: string) => {
  await page.route("**/api/case.list", (route) =>
    route.fulfill({
      status: 429,
      json: { hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } },
    }),
  );
  await page.goto(`/cases?view=ALL&q=${encodeURIComponent(prefix)}`);
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();
  const cases = page.getByRole("tabpanel", { name: /^Case view All\b/u });
  await cases.getByRole("button", { name: "Export", exact: true }).press("Enter");
  await page.getByRole("menuitem", { name: "Case report", exact: true }).press("Enter");
  await expect(page).toHaveURL(/\/cases\/report\?/u);
  const report = page.getByRole("region", { name: "Case report" });
  await expect(report.locator('[data-region="RESULTS"]')).toBeVisible();
  const grouping = report.getByRole("combobox", { name: "Group by" });
  const heading = report.getByRole("heading", { name: "Case report" });
  await expect(heading).toBeFocused();
  await page.keyboard.press("Tab");
  await expect(grouping).toBeFocused();
  await grouping.scrollIntoViewIfNeeded();
  // Open after scrolling has settled so nonmodal popup dismissal keeps its normal behavior.
  await page.evaluate(
    () =>
      new Promise<void>((done) => {
        requestAnimationFrame(() => {
          requestAnimationFrame(() => {
            done();
          });
        });
      }),
  );
  await grouping.press("Home");
  await expect(page.getByRole("listbox")).toBeVisible();
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("ArrowDown");
  await expect(page.getByRole("option", { name: "Received month" })).toBeFocused();
  await page.keyboard.press("Enter");

  const next = report.getByRole("button", { name: "Next report page" });
  const restart = report.getByRole("button", { name: "Restart report" });
  const download = report.getByRole("button", { name: "Export report CSV" });
  const exportCsv = async () => {
    const completed = page.waitForResponse(
      (reply) => new URL(reply.url()).pathname === "/api/export",
    );
    await expect(download).toBeEnabled();
    await download.click();
    const reply = await completed;
    expect(reply.status()).toBe(200);
    expect(reply.request().postDataJSON()).toEqual({
      query: "case.summary",
      selection: { view: "ALL", text: prefix, groupBy: "RECEIVED_MONTH" },
    });
    expect((await reply.text()).trimEnd().split("\r\n")).toHaveLength(52);
  };

  await expect(next).toBeVisible();
  await exportCsv();

  await next.focus();
  await page.keyboard.press("Enter");
  await expect(heading).toBeFocused();
  await page.keyboard.press("Tab");
  await expect(grouping).toBeFocused();
  await expect(report.locator('[aria-live="polite"]')).toHaveText("Report page 2");
  await expect(next).toHaveCount(0);

  await restart.focus();
  await page.keyboard.press("Enter");
  await expect(heading).toBeFocused();
  await expect(report.locator('[aria-live="polite"]')).toHaveText("Report page 1");
  await expect(next).toBeVisible();

  await page.route(summaryRoute, (route) => {
    const request = route.request().postDataJSON() as RequestByEndpoint["case.summary"];
    return request.cursor === undefined
      ? route.continue()
      : route.continue({ postData: JSON.stringify({ ...request, cursor: "bad!" }) });
  });
  await next.focus();
  await page.keyboard.press("Enter");
  const alert = report.getByRole("alert");
  await expect(alert).toContainText("This page link is no longer valid");
  await expect(alert.getByRole("button", { name: "Try again" })).toHaveCount(0);
  await expect(restart).toHaveCount(1);
  await exportCsv();
  const [restarted] = await Promise.all([
    nextSummaryRequest(page),
    alert.getByRole("button", { name: "Restart report" }).press("Enter"),
  ]);
  expect(restarted.postDataJSON()).toEqual({
    view: "ALL",
    text: prefix,
    groupBy: "RECEIVED_MONTH",
    limit: 50,
  });
  await expect(heading).toBeFocused();
  await expect(report.locator('[aria-live="polite"]')).toHaveText("Report page 1");
  await expect(next).toBeVisible();
  await page.unroute(summaryRoute);
  await verifyReportDisclosure(page);
};

test("traverses the live 51-month report and complete CSV with independent page bounds", async ({
  page,
}) => {
  test.setTimeout(120_000);
  const access = await owner(page);
  const prefix = `REPORTFIELD-${randomUUID().slice(0, 8)}`;
  for (let index = 0; index < 51; index += 1) {
    const month = new Date(Date.UTC(2018, index, 1)).toISOString().slice(0, 10);
    await execute(page, access, {
      operationId: operationId(),
      task: "case.open",
      binding: { kind: "CASE", expectedRevision: 0 },
      inputs: {
        "field:caseReference": `${prefix}-${index}`,
        "field:receivedDate": month,
        "field:claimedAmount": "1",
        "field:claimedCurrency": "EUR",
      },
    });
  }

  const selection = { text: prefix, groupBy: "RECEIVED_MONTH" as const };
  const request = { ...selection, limit: 50 };
  const first = await workRequest(page, "case.summary", request, access);
  if (first.outcome.kind !== "SUCCEEDED") {
    throw new Error("Report requires an authorized first page");
  }
  const initial = first.outcome.data;
  expect(initial.counts.total).toBe(51);
  expect(initial.rows).toHaveLength(50);
  expect(initial.rows[0]?.group).toEqual({ kind: "MONTH", value: "2018-01" });
  expect(initial.rows[49]?.group).toEqual({ kind: "MONTH", value: "2022-02" });
  if (initial.nextCursor === null) {
    throw new Error("The first page must continue");
  }
  await assertReportTail(page, access, { ...request, cursor: initial.nextCursor });
  const download = await page.request.post("/api/export", {
    headers: {
      "ClaimCore-Contract": consumerContractIdentity,
      "X-ClaimCore-Antiforgery": access.token,
      "ClaimCore-Installation": access.installationId,
      Origin: new URL(page.url()).origin,
    },
    data: { query: "case.summary", selection },
  });
  expect(download.status()).toBe(200);
  const bytes = await download.body();
  expect(Number(download.headers()["content-length"])).toBe(bytes.length);
  const lines = bytes.toString("utf8").trimEnd().split("\r\n");
  expect(lines).toHaveLength(52);
  expect(lines[0]).toBe(
    "group,count,currency,claimed,claimedCases,payable,payableCases,netPaid,netPaidCases",
  );
  expect(lines[1]).toBe("2018-01,1,EUR,1,1,,,,");
  expect(lines[51]).toBe("2022-03,1,EUR,1,1,,,,");
  await finishBrowserReport(page, prefix);
});
