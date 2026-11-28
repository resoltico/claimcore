import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import {
  consumerContractIdentity,
  type ResponseFor,
  type RequestByEndpoint,
} from "@contracts/client";
import { expect, test } from "./fixtures";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { referencePath } from "../src/routing/routes";
import { expectCaseReadsSettled } from "./case-readiness";
import {
  openAuthenticated,
  browserRequest,
  sessionToken,
  installationId,
  login,
} from "./session-helpers";
import { acceptedSubject, workRequest } from "./work-workflow";
import { chooseOption } from "./workspace-navigation";

// Secondary-principal OIDC callbacks carry private credential evidence.
test.use({ trace: "off" });

test("reconciles three cases through DATA, CLI CSV and the exact Chromium meter", async ({
  page,
  browser,
  baseURL,
}) => {
  test.setTimeout(180_000);
  await openAuthenticated(page);
  const token = await sessionToken(page);
  const installation = await installationId(page, token);
  const access = { token, installationId: installation };
  const cliFile = process.env["CLAIMCORE_WEB_E2E_CLI_FILE"];
  if (cliFile === undefined) {
    throw new Error("The published CLI fixture is required.");
  }
  const cli = JSON.parse(await readFile(cliFile, "utf8")) as {
    cli: string;
    environment: Record<string, string>;
  };
  const runCli = (...args: string[]) =>
    execFileSync("dotnet", [cli.cli, ...args], {
      env: {
        ...Object.fromEntries(
          Object.entries(process.env).filter(([name]) => !name.startsWith("CLAIMCORE_")),
        ),
        ...cli.environment,
        NO_COLOR: "1",
        XDG_CONFIG_HOME: join(dirname(cliFile), "financial-cli-config"),
        XDG_STATE_HOME: join(dirname(cliFile), "financial-cli-state"),
      },
      encoding: "utf8",
      timeout: 15_000,
    });

  const run = (...args: string[]) => runCli("case", ...args);

  const marker = `FINANCE-${randomUUID().slice(0, 8)}`;
  const day = (value: string) => `2026-08-${value}`;
  const ceiling = "999999999999999999.9999";
  const almost = "999999999999999999.9998";
  const booked = async (command: RequestByEndpoint["operation.execute"]) => {
    const preview = await workRequest(page, "operation.preview", command, access);
    expect(preview.outcome.kind).toBe("REVIEWED");
    const reply = await workRequest(page, "operation.execute", command, access);
    if (reply.outcome.kind !== "ACCEPTED") {
      throw new Error(`Expected an accepted ${command.task} operation, got ${reply.outcome.kind}.`);
    }
    const receipt = reply.outcome.data;
    expect(await acceptedSubject(page, command.operationId, access)).toEqual(receipt.subject);
    return { receipt, preview };
  };
  const open = async (suffix: string, claimed?: string) => {
    const { receipt } = await booked({
      operationId: mintOperationId(),
      task: "case.open",
      binding: { kind: "CASE", expectedRevision: 0 },
      inputs: {
        "field:policyNumber": `${marker}-${suffix}`,
        "field:receivedDate": day("01"),
        ...(claimed === undefined
          ? {}
          : { "field:claimedAmount": claimed, "field:claimedCurrency": "EUR" }),
      },
    });
    if (receipt.subject.kind !== "CASE") {
      throw new Error("Opening must identify the case.");
    }
    return {
      target: { kind: "ID" as const, caseId: receipt.subject.caseId },
      reference: receipt.subject.caseReference,
      revision: receipt.subject.revision,
    };
  };
  const a = await open("A", "0.0000");
  const b = await open("B", ceiling);
  const c = await open("C");
  const binding = (item: typeof a) => ({
    kind: "CASE" as const,
    target: item.target,
    expectedRevision: item.revision,
  });
  const change = async (
    item: typeof a,
    command: RequestByEndpoint["operation.execute"],
    balances: ReadonlyArray<readonly [string, string | null, string | null]>,
  ) => {
    const result = await booked(command);
    const { subject } = result.receipt;
    if (
      subject.kind !== "CASE" ||
      subject.snapshot.kind !== "DISCLOSED" ||
      result.preview.outcome.kind !== "REVIEWED"
    ) {
      throw new Error("Expected a disclosed case receipt and review.");
    }
    const expected = balances.map(([currency, before, after]) => ({
      currency,
      net: { before, after },
    }));
    expect(subject.snapshot.balances).toEqual(expected);
    const caseEffect = result.preview.outcome.data.review.changes.find(
      (effect) => effect.description.kind === "CASE",
    );
    expect(caseEffect?.description).toMatchObject({
      kind: "CASE",
      changes: { balances: expected },
    });
    item.revision = subject.revision;
    return subject.snapshot;
  };
  const transfer = async (
    item: typeof a,
    d: string,
    amount: string,
    currency: string,
    balances: Parameters<typeof change>[2],
    direction: "PAID" | "RETURNED" = "PAID",
    payee?: string,
  ) => {
    const id = mintOperationId();
    const result = await change(
      item,
      {
        operationId: id,
        task: "case.recordPayment",
        binding: binding(item),
        inputs: {
          "field:paymentDate": day(d),
          "field:paidAmount": amount,
          "field:paidCurrency": currency,
          "input:direction": direction,
          ...(payee === undefined ? {} : { "input:payee": payee }),
        },
      },
      balances,
    );
    expect(result.payments.map(({ entry, kind }) => [kind, entry.paymentId])).toEqual([
      ["RECORDED", id],
    ]);
    return id;
  };
  const decide = (item: typeof a, amount: string) =>
    change(
      item,
      {
        operationId: mintOperationId(),
        task: "case.decide",
        binding: binding(item),
        inputs: {
          "field:paymentDecisionDate": day("04"),
          "field:payableAmount": amount,
          "field:payableCurrency": "EUR",
        },
      },
      [],
    );
  const remove = (item: typeof a, id: string, balances: Parameters<typeof change>[2]) =>
    change(
      item,
      {
        operationId: mintOperationId(),
        task: "case.removePayment",
        binding: { ...binding(item), paymentId: id },
        inputs: {},
      },
      balances,
    );

  const cells = (csv: string) => {
    const [header, ...rows] = csv
      .trimEnd()
      .split("\r\n")
      .map((line) => line.split(","));
    if (header === undefined) {
      throw new Error("Expected a CSV header.");
    }
    return rows.map((row) => Object.fromEntries(header.map((name, i) => [name, row[i]])));
  };
  const check = async (
    euro: string | undefined,
    contributions: number,
    payable: string,
    payableCases: number,
    latestA: string,
    extras: Record<string, string> = {},
  ) => {
    const report = await workRequest(
      page,
      "case.summary",
      { text: marker, groupBy: "RECEIVED_MONTH", limit: 50 },
      access,
    );
    if (report.outcome.kind !== "SUCCEEDED") {
      throw new Error("Expected the grouped report.");
    }
    expect(report.outcome.data.counts.total).toBe(3);
    expect(report.outcome.data.rows.map((row) => [row.group, row.count])).toEqual(
      report.outcome.data.rows.map(() => [{ kind: "MONTH", value: "2026-08" }, 3]),
    );
    const row = report.outcome.data.rows.find(({ currency }) => currency === "EUR");
    expect(row?.claimed).toEqual({ currency: "EUR", amount: ceiling, cases: 2 });
    expect(row?.payable).toEqual({ currency: "EUR", amount: payable, cases: payableCases });
    expect(row?.netPaid).toEqual(
      euro === undefined ? undefined : { currency: "EUR", amount: euro, cases: contributions },
    );
    expect(
      Object.fromEntries(
        report.outcome.data.rows
          .filter(({ currency }) => currency !== "EUR")
          .map((item) => [item.currency, item.netPaid?.amount]),
      ),
    ).toEqual(extras);

    const list = cells(run("list", "--text", marker, "--limit", "1", "--all", "--format", "csv"));
    expect(new Set(list.map((item) => item["caseId"]))).toEqual(
      new Set([a.target.caseId, b.target.caseId, c.target.caseId]),
    );
    expect(list.filter((item) => item["caseId"] === c.target.caseId)).toMatchObject([
      { currency: "", claimed: "", payable: "", netPaid: "" },
    ]);
    expect(
      list
        .filter((item) => item["caseId"] === a.target.caseId)
        .map((item) => item["latestTransferDate"]),
    ).toEqual(list.filter((item) => item["caseId"] === a.target.caseId).map(() => latestA));
    const csvReport = cells(
      run("summary", "--group-by", "received-month", "--text", marker, "--format", "csv"),
    );
    expect(csvReport.find((item) => item["currency"] === "EUR")).toMatchObject({
      count: "3",
      claimed: ceiling,
      claimedCases: "2",
      payable,
      payableCases: String(payableCases),
      netPaid: euro ?? "",
      netPaidCases: euro === undefined ? "" : String(contributions),
    });
  };
  const detail = async (item: typeof a) => {
    await page.goto(referencePath(item.reference));
    await expectCaseReadsSettled(page);
    return page.locator(".case-detail");
  };

  const expectHeadline = async (
    item: typeof a,
    expectedHeadline: "NO_PAYMENT_DUE" | "PAYABLE_COVERED" | "OVERPAID",
    label: string,
  ) => {
    const current = await workRequest(page, "case.get", { target: item.target }, access);
    expect(current.outcome).toMatchObject({
      kind: "SUCCEEDED",
      data: { headline: expectedHeadline },
    });
    const listed = await workRequest(page, "case.list", { limit: 10, text: marker }, access);
    if (listed.outcome.kind !== "SUCCEEDED") {
      throw new Error("Expected the selected case on the list.");
    }
    expect(
      listed.outcome.data.items.find(({ caseId }) => caseId === item.target.caseId)?.headline,
    ).toBe(expectedHeadline);
    const row = cells(run("list", "--text", marker, "--format", "csv")).find(
      (record) => record["caseId"] === item.target.caseId,
    );
    expect(row?.["headline"]).toBe(expectedHeadline);
    await expect(
      (await detail(item)).locator(".case-title-line").getByText(label, { exact: true }),
    ).toBeVisible();
    expect(run("get", item.reference, "--format", "text")).toContain(label);
  };

  const zeroDecision = {
    task: "case.decide",
    binding: binding(a),
    operationId: mintOperationId(),
    inputs: {
      "field:paymentDecisionDate": day("04"),
      "field:payableAmount": "0",
      "field:payableCurrency": "EUR",
    },
  } as const;
  await change(a, zeroDecision, []);
  await check(undefined, 0, "0", 1, "");
  await expectHeadline(a, "NO_PAYMENT_DUE", "No payment due");
  const expectCoverageFact = async (endpoint: "case.get" | "operation.get", input: unknown) => {
    const reply = await browserRequest(page, `/api/${endpoint}`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-ClaimCore-Antiforgery": token,
        "ClaimCore-Installation": installation,
        "ClaimCore-Representation": "HUMAN",
      },
      body: JSON.stringify(input),
    });
    expect(reply.status).toBe(200);
    const { outcome } = reply.payload as ResponseFor<typeof endpoint, "HUMAN">;
    if (outcome.kind !== "SUCCEEDED" && outcome.kind !== "ACCEPTED") {
      throw new Error("Expected a disclosed coverage fact.");
    }
    const facts = outcome.document.screen.regions.flatMap(({ content }) =>
      content.kind === "ENTRIES" ? content.entries : [],
    );
    expect(facts.find(({ key }) => key === "progress")?.value).toMatchObject({
      kind: "STATE",
      vocabulary: "headline",
      token: "NO_PAYMENT_DUE",
    });
  };
  await expectCoverageFact("case.get", { target: a.target });
  await expectCoverageFact("operation.get", { operationId: zeroDecision.operationId });
  expect(run("get", a.reference, "--format", "text", "--details")).toMatch(
    /progress: No payment due/iu,
  );
  expect(
    runCli("operation", "get", zeroDecision.operationId, "--format", "text", "--details"),
  ).toMatch(/progress: No payment due/iu);
  const party = await booked({
    operationId: mintOperationId(),
    task: "party.add",
    binding: { kind: "PARTY", target: a.target },
    inputs: { "input:partyRole": "OTHER", "input:partyName": "Synthetic recipient" },
  });
  const recipient = party.receipt.subject;
  if (recipient.kind !== "PARTY") {
    throw new Error("Expected the recipient identity.");
  }
  const eur = await transfer(
    a,
    "05",
    "10",
    "EUR",
    [["EUR", null, "10"]],
    "PAID",
    recipient.partyId,
  );
  await check("10", 1, "0", 1, day("05"));
  await expectHeadline(a, "OVERPAID", "Overpaid");
  const usd = await transfer(a, "06", "1.2345", "USD", [["USD", null, "1.2345"]]);
  await check("10", 1, "0", 1, day("06"), { USD: "1.2345" });
  const returned = await transfer(
    a,
    "07",
    "12.0001",
    "EUR",
    [["EUR", "10", "-2.0001"]],
    "RETURNED",
  );
  await check("-2.0001", 1, "0", 1, day("07"), { USD: "1.2345" });
  const cross = mintOperationId();
  const corrected = await change(
    a,
    {
      operationId: cross,
      task: "case.correctPayment",
      binding: { ...binding(a), paymentId: returned },
      inputs: {
        "field:paymentDate": day("08"),
        "field:paidAmount": "1.2345",
        "field:paidCurrency": "USD",
      },
    },
    [
      ["EUR", "-2.0001", "10"],
      ["USD", "1.2345", "0"],
    ],
  );
  expect(
    corrected.payments.map(({ kind, entry }) => [kind, entry.paymentId, entry.direction]),
  ).toEqual([
    ["REMOVED", returned, "RETURNED"],
    ["RECORDED", cross, "RETURNED"],
  ]);
  await check("10", 1, "0", 1, day("08"), { USD: "0" });

  let cursor: string | null = null;
  const effects = [];
  do {
    const history: ResponseFor<"case.timeline", "DATA"> = await workRequest(
      page,
      "case.timeline",
      {
        target: a.target,
        filter: "FACTS",
        limit: 1,
        ...(cursor === null ? {} : { cursor }),
      },
      access,
    );
    if (history.outcome.kind !== "SUCCEEDED") {
      throw new Error("Expected full Activity.");
    }
    effects.push(...history.outcome.data.items.filter((item) => item.operationId === cross));
    cursor = history.outcome.data.nextCursor;
  } while (effects.length < 3 && cursor !== null);
  expect(effects.map((item) => item.subject.partKind).sort()).toEqual([
    "CASE",
    "PAYMENT",
    "PAYMENT",
  ]);
  expect(
    effects.flatMap((item) =>
      item.changes.kind === "DISCLOSED"
        ? item.changes.items.filter(({ kind }) => kind === "BALANCES")
        : [],
    ),
  ).toHaveLength(1);

  await booked({
    operationId: mintOperationId(),
    task: "party.remove",
    binding: {
      kind: "PARTY",
      target: a.target,
      partyId: recipient.partyId,
      expectedRevision: recipient.revision,
    },
    inputs: {},
  });
  const amend = mintOperationId();
  const correction = {
    operationId: amend,
    task: "case.correctPayment",
    binding: { ...binding(a), paymentId: eur },
    inputs: { "input:reference": "=synthetic" },
  } as const;
  const amended = await change(a, correction, []);
  expect(amended.payments[1]?.entry.payee).toBe(recipient.partyId);
  const payments = await workRequest(
    page,
    "case.payments",
    { target: a.target, limit: 20 },
    access,
  );
  expect(payments.outcome).toMatchObject({
    kind: "SUCCEEDED",
    data: { payees: [{ id: recipient.partyId, name: "Synthetic recipient", removed: true }] },
  });
  await check("10", 1, "0", 1, day("08"), { USD: "0" });

  const refused = async (
    command: RequestByEndpoint["operation.execute"],
    code: string,
    issue?: string,
  ) => {
    const result = await browserRequest(page, "/api/operation.execute", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-ClaimCore-Antiforgery": token,
        "ClaimCore-Installation": installation,
      },
      body: JSON.stringify(command),
    });
    expect(result.payload).toMatchObject({
      outcome: {
        kind: "REJECTED",
        rejection: { code, ...(issue === undefined ? {} : { issues: [{ code: issue }] }) },
      },
    });
  };
  await refused(
    { ...correction, operationId: mintOperationId(), binding: { ...binding(a), paymentId: amend } },
    "CORRECTION_NO_CHANGES",
  );

  const jpy = await transfer(a, "11", "7.0001", "JPY", [["JPY", null, "7.0001"]]);
  await check("10", 1, "0", 1, day("11"), { JPY: "7.0001", USD: "0" });
  await remove(a, jpy, [["JPY", "7.0001", null]]);
  await check("10", 1, "0", 1, day("08"), { USD: "0" });
  await remove(a, usd, [["USD", "0", "-1.2345"]]);
  await check("10", 1, "0", 1, day("08"), { USD: "-1.2345" });
  await remove(a, cross, [["USD", "-1.2345", null]]);
  await check("10", 1, "0", 1, day("05"));

  for (const invalid of ["1000000000000000000", "0.00001", "0"]) {
    await refused(
      {
        operationId: mintOperationId(),
        task: "case.recordPayment",
        binding: binding(b),
        inputs: {
          "field:paymentDate": day("02"),
          "field:paidAmount": invalid,
          "field:paidCurrency": "EUR",
        },
      },
      "INVALID_INPUT",
    );
  }
  await transfer(b, "02", almost, "EUR", [["EUR", null, almost]]);
  await check("1000000000000000009.9998", 2, "0", 1, day("05"));
  await expect((await detail(b)).locator("meter")).toHaveCount(0);
  await decide(b, ceiling);
  await check("1000000000000000009.9998", 2, ceiling, 2, day("05"));
  const near = await detail(b);
  const caption = "Net paid 999,999,999,999,999,999.9998 EUR of 999,999,999,999,999,999.9999 EUR";
  await expect(near.locator("meter")).toHaveCount(1);
  await expect(near.getByRole("meter", { name: caption })).toHaveAttribute("value", "999");
  await expect(near.locator("meter")).toHaveAttribute("max", "1000");
  expect(run("get", b.reference, "--format", "text")).toContain(caption);
  await transfer(b, "09", "0.0001", "EUR", [["EUR", almost, ceiling]]);
  await check("1000000000000000009.9999", 2, ceiling, 2, day("05"));
  await expectHeadline(b, "PAYABLE_COVERED", "Payable covered");
  const covered = page.locator(".case-detail");
  await expect(covered.locator("meter")).toHaveCount(1);
  await expect(covered.locator("meter")).toHaveAttribute("value", "1000");
  await expect(
    covered.locator(".case-stage-list").getByText("Payable covered", { exact: true }),
  ).toBeVisible();
  await transfer(b, "10", ceiling, "EUR", [["EUR", ceiling, "1999999999999999999.9998"]]);
  await check("2000000000000000009.9998", 2, ceiling, 2, day("05"));
  await expect((await detail(b)).locator("meter")).toHaveCount(0);
  const activity = run("timeline", a.reference, "--format", "csv", "--all");
  expect(activity).toContain(",netPaid,10,-2.0001,EUR,EUR\r\n");
  expect(activity).toContain(",'=synthetic,,\r\n");

  await decide(c, ceiling);
  await transfer(c, "09", "0.0001", "EUR", [["EUR", null, "0.0001"]]);
  const smallest = await detail(c);
  await expect(smallest.locator("meter")).toHaveCount(1);
  await expect(smallest.getByRole("meter", { name: /Net paid 0\.0001 EUR/u })).toHaveAttribute(
    "value",
    "1",
  );

  const qualifyPaymentUpdates = async () => {
    const other = (
      await booked({
        operationId: mintOperationId(),
        task: "party.add",
        binding: { kind: "PARTY", target: a.target },
        inputs: { "input:partyRole": "OTHER", "input:partyName": "Temporary recipient" },
      })
    ).receipt.subject;
    if (other.kind !== "PARTY") {
      throw new Error("Expected the temporary recipient.");
    }
    const replacement = {
      operationId: mintOperationId(),
      task: "case.correctPayment",
      binding: { ...binding(a), paymentId: amend },
      inputs: { "input:payee": other.partyId },
    } as const;
    const reviewed = await workRequest(page, "operation.preview", replacement, access);
    expect(reviewed.outcome.kind).toBe("REVIEWED");
    await booked({
      operationId: mintOperationId(),
      task: "party.remove",
      binding: {
        kind: "PARTY",
        target: a.target,
        partyId: other.partyId,
        expectedRevision: other.revision,
      },
      inputs: {},
    });
    await refused(replacement, "INVALID_INPUT", "PAYEE_UNAVAILABLE");

    const payeeCase = await detail(a);
    await payeeCase
      .locator('[data-region="PAYMENTS"] tbody tr')
      .filter({ hasText: "10.00 EUR" })
      .locator("button[aria-haspopup]")
      .click();
    await page.getByRole("menuitem", { name: "Correct payment entry", exact: true }).click();
    const correctionEditor = page.getByRole("dialog");
    const editPayee = correctionEditor.getByRole("checkbox", { name: "Change Payee", exact: true });
    const reload = correctionEditor.getByRole("button", {
      name: "Reload current case",
      exact: true,
    });
    await expect(editPayee.or(reload)).toBeVisible();
    if (await reload.isVisible()) {
      await reload.click();
    }
    await editPayee.check();
    const payeeControl = correctionEditor.getByRole("combobox", { name: "Payee", exact: true });
    await expect(payeeControl).toHaveText("Keep current selection");
    await chooseOption(page, payeeControl, recipient.partyId);
    await expect(payeeControl).toHaveText("Keep current selection");

    const clearedId = mintOperationId();
    const cleared = await change(
      a,
      {
        operationId: clearedId,
        task: "case.correctPayment",
        binding: { ...binding(a), paymentId: amend },
        inputs: { "input:payee": null },
      },
      [],
    );
    expect(cleared.payments[1]?.entry.payee).toBeNull();

    let selected = clearedId;
    for (const [flag, value, amount, currency] of [
      ["--paid-amount", "10.1", "10.1", "EUR"],
      ["--paid-currency", "USD", "10.1", "USD"],
    ] as const) {
      const response: unknown = JSON.parse(
        run(
          "correct-payment",
          "--case-id",
          a.target.caseId,
          "--payment-id",
          selected,
          "--expected-revision",
          String(a.revision),
          flag,
          value,
          "--format",
          "json",
        ),
      );
      expect(response).toMatchObject({ result: { outcome: { kind: "ACCEPTED" } } });
      const read = await workRequest(page, "case.get", { target: a.target }, access);
      if (read.outcome.kind !== "SUCCEEDED") {
        throw new Error("Expected the accepted correction.");
      }
      expect(read.outcome.data.revision).toBe(a.revision + 1);
      expect(read.outcome.data.latestPayments).toMatchObject([
        { money: { amount, currency }, payee: null },
      ]);
      const [entry] = read.outcome.data.latestPayments;
      if (entry === undefined) {
        throw new Error("Expected the replacement payment.");
      }
      expect(entry.paymentId).not.toBe(selected);
      selected = entry.paymentId;
      a.revision = read.outcome.data.revision;
    }
  };
  await qualifyPaymentUpdates();

  if (baseURL === undefined) {
    throw new Error("Expected the published workspace URL.");
  }
  const reader = await browser.newContext({ baseURL, ignoreHTTPSErrors: true });
  try {
    const observer = await reader.newPage();
    await observer.goto("/", { waitUntil: "commit" });
    await login(observer, "Cases", "Sign in", "synthetic-reader");
    const view = await workRequest(observer, "case.get", { target: a.target });
    expect(view.outcome).toMatchObject({ kind: "SUCCEEDED", data: { advice: { acts: [] } } });
    const observerToken = await sessionToken(observer);
    const csv = await observer.request.post("/api/export", {
      headers: {
        "ClaimCore-Contract": consumerContractIdentity,
        "ClaimCore-Installation": installation,
        "X-ClaimCore-Antiforgery": observerToken,
        Origin: new URL(observer.url()).origin,
      },
      data: { query: "case.list", selection: { text: marker } },
    });
    expect(csv.status()).toBe(200);
    expect(new Set(cells(await csv.text()).map(({ caseId }) => caseId))).toEqual(
      new Set([a.target.caseId, b.target.caseId, c.target.caseId]),
    );
  } finally {
    await reader.close();
  }
});
