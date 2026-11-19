import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
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

test("renders both related directions together and retains self-rebind review notices", async ({
  page,
}) => {
  test.setTimeout(120_000);
  const access = await owner(page);
  const prefix = `LINKFIELD-${randomUUID()}`;
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
    if (preview.outcome.kind !== "REVIEWED") {
      throw new Error("The eligible rebind must be reviewable");
    }
    const notices = preview.outcome.review.document.screen?.regions.filter(
      (item) => item.role === "NOTICE",
    );
    expect(notices).toHaveLength(1);
    expect(notices?.[0]?.content).toEqual({
      kind: "NOTICE",
      notices: [
        { messageId: "task.people.changeSignInIdentity.meaning", tone: "INFO" },
        ...(self ? [{ messageId: "notice.changesOwnSignInIdentity", tone: "WARNING" }] : []),
      ],
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

test("traverses the live 51-month report and complete CSV with independent page bounds", async ({
  page,
}) => {
  test.setTimeout(120_000);
  const access = await owner(page);
  const prefix = `REPORTFIELD-${randomUUID()}`;
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
});
