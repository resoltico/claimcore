import { selectedActor, applyPeople, syntheticPrincipals } from "./people-setup";
import { prefixedInputs } from "../tests/support/submission-inputs";
import {
  type ResponseFor,
  endpoints,
  type EndpointId,
  type RequestByEndpoint,
  type DataResponseByEndpoint,
  type OperationInputs_work_update,
} from "@contracts/client";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { randomUUID } from "node:crypto";
import { registration as registrationInput } from "../tests/support/inputs";
import type { Browser, BrowserContext, Page } from "@playwright/test";
import { expect } from "@playwright/test";
import { referencePath, actPath } from "../src/routing/routes";

import { browserRequest, openAuthenticated, sessionToken, installationId } from "./session-helpers";
const request = async <K extends EndpointId>(
  page: Page,
  endpoint: K,
  body: RequestByEndpoint[K],
) => {
  const metadata = endpoints.find((entry) => entry.id === endpoint)!;
  const antiforgery = await sessionToken(page);
  const binding = metadata.requiresInstallation
    ? {
        "ClaimCore-Installation": await installationId(page, antiforgery),
      }
    : {};
  const reply = await browserRequest(page, metadata.path, {
    method: metadata.method,
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": antiforgery,
      ...binding,
    },
    body: JSON.stringify(body),
  });
  const endpointReply = reply.payload as ResponseFor<typeof endpoint, "DATA">;
  if (
    reply.status !== 200 ||
    !(!("representation" in endpointReply) || endpointReply.representation === "DATA")
  ) {
    throw new Error(`E2E_MATRIX_SEED_${endpoint}_REPLY`);
  }
  return endpointReply;
};
const execute = async (page: Page, body: RequestByEndpoint["operation.execute"]) => {
  const reply = await request(page, "operation.execute", body);
  if (reply.outcome.kind !== "ACCEPTED") {
    throw new Error(`E2E_MATRIX_SEED_${reply.outcome.kind}`);
  }
  return reply.outcome.data;
};
type CaseSubmission = Extract<
  RequestByEndpoint["operation.execute"],
  { binding: { kind: "CASE" } }
>;
const caseAct = (
  page: Page,
  reference: string,
  revision: number,
  authored: Pick<CaseSubmission, "task" | "inputs">,
) =>
  execute(page, {
    ...authored,
    operationId: mintOperationId(),
    binding: {
      kind: "CASE",
      target: { kind: "REFERENCE", caseReference: reference },
      expectedRevision: revision,
    },
  } as CaseSubmission);
const workAct = (
  page: Page,
  reference: string,
  revision: number,
  inputs: OperationInputs_work_update,
) =>
  execute(page, {
    operationId: mintOperationId(),
    task: "work.update",
    binding: {
      kind: "WORK",
      target: { kind: "REFERENCE", caseReference: reference },
      expectedRevision: revision,
    },
    inputs,
  });
const planReminder = (page: Page, reference: string, date: string) =>
  execute(page, {
    operationId: mintOperationId(),
    task: "reminder.create",
    binding: {
      kind: "REMINDER",
      target: {
        kind: "REFERENCE",
        caseReference: reference,
      },
    },
    inputs: { "input:reminderAction": "Review synthetic case", "input:reminderDueDate": date },
  });
const states = ["undecided", "decided", "paid", "closed", "voided", "erased"] as const;
type CaseState = (typeof states)[number];
export type MatrixRoute = {
  id: string;
  path: string;
  ready: string;
  endpoint?: EndpointId;
  expectedInput?: Readonly<Record<string, unknown>>;
  filtered?: boolean;
  summaryExpanded?: boolean;
  expectedReference?: string;
};
const views = [
  {
    id: "open-undecided",
    path: "/cases?view=UNDECIDED",
    input: {
      view: "UNDECIDED",
    },
  },
  {
    id: "decided-unpaid",
    path: "/cases?view=DECIDED_UNPAID",
    input: {
      view: "DECIDED_UNPAID",
    },
  },
  {
    id: "recent",
    path: "/cases?view=ALL&order=RECENT",
    input: {
      view: "ALL",
      order: "RECENT",
    },
  },
  {
    id: "closed",
    path: "/cases?view=CLOSED",
    input: {
      view: "CLOSED",
    },
  },
  {
    id: "voided",
    path: "/cases?view=VOIDED",
    input: {
      view: "VOIDED",
    },
  },
  {
    id: "all",
    path: "/cases?view=ALL",
    input: {
      view: "ALL",
    },
  },
] as const;
const worklistRoutes = (marker: string, mineReference: string): MatrixRoute[] => [
  {
    id: "cases-mine",
    expectedReference: mineReference,
    path: "/cases?view=MINE",
    ready: "#case-list-title",
    endpoint: "case.list",
    expectedInput: {
      limit: 50,
      view: "MINE",
    },
  },
  {
    id: "cases-notified",
    path: "/cases?view=ALL&order=NOTIFIED",
    ready: "#case-list-title",
    endpoint: "case.list",
    expectedInput: {
      limit: 50,
      view: "ALL",
      order: "NOTIFIED",
    },
  },
  {
    id: "cases-summary",
    path: `/cases?view=ALL&q=${marker}`,
    ready: "#case-list-title",
    endpoint: "case.list",
    expectedInput: {
      limit: 50,
      view: "ALL",
      text: marker,
    },
    summaryExpanded: true,
  },
  ...views.map((view) => ({
    id: `cases-${view.id}`,
    path: view.path,
    ready: "#case-list-title",
    endpoint: "case.list" as const,
    expectedInput: {
      limit: 50,
      ...view.input,
    },
  })),
  {
    id: "cases-filtered",
    path: `/cases?${new URLSearchParams({
      view: "ALL",
      lifecycle: "ACTIVE",
      status: "OPENED",
      progress: "UNDECIDED",
      order: "RECENT",
      q: marker,
    })}`,
    ready: "#case-list-title",
    endpoint: "case.list",
    expectedInput: {
      view: "ALL",
      lifecycle: ["ACTIVE"],
      status: ["OPENED"],
      progress: ["UNDECIDED"],
      order: "RECENT",
      text: marker,
      limit: 50,
    },
    filtered: true,
  },
];
const caseRoutes = (references: Record<CaseState, string>): MatrixRoute[] => [
  ...states.map((state) => ({
    id: `case-${state}`,
    path: referencePath(references[state]),
    ready: "#case-detail-title",
    endpoint: "case.get" as const,
    expectedInput: {
      target: {
        kind: "REFERENCE",
        caseReference: references[state],
      },
    },
  })),
  {
    id: "editor-open",
    path: "/cases/new",
    ready: '[role="dialog"] .ui-dialog-title',
  },
  ...(["case.decide", "case.correct", "case.void"] as const).map((act) => ({
    id: `editor-${act.toLowerCase()}`,
    path: actPath(
      {
        kind: "REFERENCE",
        caseReference: act === "case.correct" ? references.paid : references.undecided,
      },
      act,
    ),
    ready: '[role="dialog"] .ui-dialog-title',
    endpoint: "case.get" as const,
  })),
  {
    id: "editor-work",
    path: referencePath(references.undecided),
    ready: "#case-detail-title",
    endpoint: "case.get",
  },
];
const routes = (data: {
  references: Record<CaseState, string>;
  operationId: string;
  actorId: string;
  marker: string;
}): MatrixRoute[] => [
  ...worklistRoutes(data.marker, data.references.undecided),
  ...caseRoutes(data.references),
  {
    id: "reminders",
    path: "/reminders",
    ready: "#reminder-queue-title",
    endpoint: "reminder.list",
  },
  {
    id: "recovery",
    path: "/recovery",
    ready: "#recovery-title",
  },
  {
    id: "paid-correction-review",
    path: actPath(
      {
        kind: "REFERENCE",
        caseReference: data.references.paid,
      },
      "case.correct",
    ),
    ready: '[role="dialog"] .ui-dialog-title',
    endpoint: "case.get",
  },
  {
    id: "record-payment-review",
    path: actPath(
      {
        kind: "REFERENCE",
        caseReference: data.references.decided,
      },
      "case.recordPayment",
    ),
    ready: '[role="dialog"] .ui-dialog-title',
    endpoint: "case.get",
  },
  {
    id: "history-comparison",
    path: referencePath(data.references.paid),
    ready: "#case-detail-title",
    endpoint: "case.get",
  },
  {
    id: "operation-result",
    path: `/recovery/${encodeURIComponent(data.operationId)}`,
    ready: "#operation-lookup-title",
    endpoint: "operation.get",
  },
  {
    id: "people-history",
    path: "/people-history",
    ready: "#people-history-title",
    endpoint: "people.history",
    expectedInput: {
      limit: 50,
    },
  },
  {
    id: "not-found",
    path: "/not-a-claimcore-page",
    ready: ".missing-page h1",
  },
  {
    id: "people-list",
    path: "/people",
    ready: "#people-title",
    endpoint: "people.list",
  },
  {
    id: "people-detail",
    path: `/people/${encodeURIComponent(data.actorId)}`,
    ready: '[role="dialog"] .ui-dialog-title',
    endpoint: "people.grants",
  },
];
const findOwner = async (
  page: Page,
  target: {
    issuer: string;
    subject: string;
  },
) => {
  let cursor: string | null = null;
  do {
    const listing: DataResponseByEndpoint["people.list"] = await request(page, "people.list", {
      limit: 50,
      ...(cursor === null
        ? {}
        : {
            cursor,
          }),
    });
    if (listing.outcome.kind !== "SUCCEEDED") {
      throw new Error("E2E_MATRIX_PEOPLE_LIST");
    }
    const owner = listing.outcome.data.items.find(
      (actor) =>
        actor.principal.kind === "HUMAN" &&
        actor.principal.issuer === target.issuer &&
        actor.principal.subject === target.subject,
    );
    if (owner !== undefined) {
      return owner;
    }
    cursor = listing.outcome.data.nextCursor;
  } while (cursor !== null);
  throw new Error("E2E_MATRIX_OWNER_NOT_REGISTERED");
};
const hasStewardship = async (page: Page, actorId: string) => {
  const grants = await request(page, "people.grants", {
    actorId,
    limit: 50,
  });
  if (grants.outcome.kind !== "SUCCEEDED" || grants.outcome.data.nextCursor !== null) {
    throw new Error("E2E_MATRIX_OWNER_GRANTS_NOT_COMPLETE");
  }
  return grants.outcome.data.items.some(
    (grant) => grant.role === "DATA_STEWARD" && grant.scope.kind === "INSTALLATION",
  );
};
const ownerStewardship = async (page: Page) => {
  const inventory = await syntheticPrincipals();
  const target = {
    kind: "HUMAN",
    issuer: inventory.issuer,
    subject: inventory.ownerSubject,
  } as const;
  const owner = await findOwner(page, target);
  const existing = await hasStewardship(page, owner.actor.actorId);
  const set = async (active: boolean) =>
    applyPeople(page, {
      operationId: mintOperationId(),
      task: active ? "people.grant" : "people.revoke",
      binding: { kind: "PEOPLE", actorId: await selectedActor(page, target) },
      inputs: { "input:role": "DATA_STEWARD", "input:scope": "INSTALLATION" },
    });
  return {
    inventory,
    enable: async () => {
      if (!existing) {
        await set(true);
      }
    },
    restore: async () => {
      if (!existing && (await hasStewardship(page, owner.actor.actorId))) {
        await set(false);
      }
    },
  };
};
const advanceCase = async (page: Page, reference: string, state: CaseState, date: string) => {
  if (state === "decided" || state === "paid") {
    await caseAct(page, reference, 1, {
      task: "case.decide",
      inputs: {
        "field:paymentDecisionDate": date,
        "field:payableAmount": "300.25",
        "field:payableCurrency": "EUR",
      },
    });
  }
  if (state === "paid") {
    return (
      await caseAct(page, reference, 2, {
        task: "case.recordPayment",
        inputs: {
          "field:paymentDate": date,
          "field:paidAmount": "300.25",
          "field:paidCurrency": "EUR",
        },
      })
    ).operationId;
  }
  if (state === "closed") {
    await caseAct(page, reference, 1, { task: "case.close", inputs: {} });
  }
  if (state === "voided") {
    await caseAct(page, reference, 1, {
      task: "case.void",
      inputs: { "input:reason": "Synthetic matrix entry error" },
    });
  }
  if (state === "erased") {
    await caseAct(page, reference, 1, { task: "case.erase", inputs: {} });
  }
  return null;
};
const assertCase = async (page: Page, reference: string, state: CaseState) => {
  const read = await request(page, "case.get", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
  });
  if (read.outcome.kind !== "SUCCEEDED") {
    throw new Error(`E2E_MATRIX_CASE_${state}_UNAVAILABLE`);
  }
  const { data: value } = read.outcome;
  let lifecycle = "ACTIVE";
  if (state === "voided") {
    lifecycle = "VOIDED";
  }
  if (state === "erased") {
    lifecycle = "ERASED";
  }
  expect(value.state.kind).toBe(lifecycle);
  if (value.state.kind !== "ERASED") {
    expect(value.state.fields.status).toBe(state === "closed" ? "CLOSED" : "OPENED");
    expect(value.state.fields.finance.decision !== null).toBe(
      state === "decided" || state === "paid",
    );
    expect(value.state.fields.finance.paymentCount > 0).toBe(state === "paid");
    if (state === "paid") {
      expect(value.advice.acts).toContain("case.correct");
    }
  }
};
const seedCases = async (page: Page, marker: string, date: string) => {
  const references = Object.fromEntries(
    states.map((state) => [state, state === "undecided" ? `~${marker}/?#` : `${marker}-${state}`]),
  ) as Record<CaseState, string>;
  const registration = registrationInput({
    incidentDate: date,
    incidentNotificationDate: date,
    incidentCountry: "LV",
    claimantName: `${marker} Rīga العربية`,
    insurerName: "Matrix insurer Apdrošinātājs شركة التأمين",
    claimedAmount: "1234.56",
    claimedCurrency: "EUR",
  });
  let operationId = "";
  for (const state of states) {
    const reference = references[state];
    await caseAct(page, reference, 0, {
      task: "case.open",
      inputs: { ...prefixedInputs("field:", registration, true) },
    });
    const acceptedId = await advanceCase(page, reference, state, date);
    if (acceptedId !== null) {
      operationId = acceptedId;
    }
    await assertCase(page, reference, state);
    if (state === "decided") {
      await caseAct(page, reference, 2, {
        task: "case.recordPayment",
        inputs: {
          "field:paymentDate": date,
          "field:paidAmount": "100",
          "field:paidCurrency": "EUR",
        },
      });
      await execute(page, {
        operationId: mintOperationId(),
        task: "note.add",
        binding: {
          kind: "NOTE",
          target: {
            kind: "REFERENCE",
            caseReference: reference,
          },
        },
        inputs: {
          "input:noteText":
            "Documents received. Review the remaining payment after the claimant confirms the estimate.",
        },
      });
    }
  }
  return {
    references,
    operationId,
  };
};
const registerActor = async (page: Page, issuer: string, reference: string) => {
  const target = {
    kind: "SERVICE",
    issuer,
    subject: `matrix-${randomUUID()}`,
  } as const;
  const registered = await execute(page, {
    operationId: mintOperationId(),
    task: "people.register",
    binding: { kind: "PEOPLE" },
    inputs: {
      "input:profile.displayName": "Synthetic matrix account",
      ...prefixedInputs("input:principal.", target),
    },
  });
  if (registered.subject.kind !== "PEOPLE") {
    throw new Error("E2E_MATRIX_PEOPLE_RECEIPT");
  }
  await applyPeople(page, {
    operationId: mintOperationId(),
    task: "people.grant",
    binding: { kind: "PEOPLE", actorId: await selectedActor(page, target) },
    inputs: {
      "input:role": "CASE_READER",
      "input:scope": "CASE",
      "field:caseReference": reference,
    },
  });
  return registered.subject.targetActor.kind === "DISCLOSED"
    ? registered.subject.targetActor.actor.actorId
    : registered.subject.targetActor.actorId;
};
const setOwnerCaseWork = async (
  page: Page,
  actorId: string,
  reference: string,
  date: string,
  restore: () => Promise<void>,
) => {
  await workAct(page, reference, 0, { "field:responsibleActor": actorId });
  await planReminder(page, reference, date);
  const assigned = await request(page, "case.get", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
  });
  if (assigned.outcome.kind !== "SUCCEEDED") {
    throw new Error("Matrix case work must be readable");
  }
  expect(assigned.outcome.data.mine).toBe(true);
  expect(assigned.outcome.data.work.responsible?.actorId).toBe(actorId);
  return async () => {
    try {
      await workAct(page, reference, 1, { "field:responsibleActor": null });
    } finally {
      await restore();
    }
  };
};
const unavailableCaseWork = async (page: Page, issuer: string, reference: string, date: string) => {
  const target = {
    kind: "HUMAN",
    issuer,
    subject: `matrix-assignee-${randomUUID()}`,
  } as const;
  const registered = await execute(page, {
    operationId: mintOperationId(),
    task: "people.register",
    binding: { kind: "PEOPLE" },
    inputs: {
      "input:profile.displayName": "Synthetic matrix account",
      ...prefixedInputs("input:principal.", target),
    },
  });
  if (registered.subject.kind !== "PEOPLE") {
    throw new Error("Expected the independently registered human assignee");
  }
  const actorId =
    registered.subject.targetActor.kind === "DISCLOSED"
      ? registered.subject.targetActor.actor.actorId
      : registered.subject.targetActor.actorId;
  await applyPeople(page, {
    operationId: mintOperationId(),
    task: "people.grant",
    binding: { kind: "PEOPLE", actorId: await selectedActor(page, target) },
    inputs: {
      "input:role": "CASE_HANDLER",
      "input:scope": "CASE",
      "field:caseReference": reference,
    },
  });
  const yesterday = new Date(`${date}T12:00:00Z`);
  yesterday.setUTCDate(yesterday.getUTCDate() - 1);
  await workAct(page, reference, 0, { "field:responsibleActor": actorId });
  await planReminder(page, reference, yesterday.toISOString().slice(0, 10));
  await applyPeople(page, {
    operationId: mintOperationId(),
    task: "people.disable",
    binding: { kind: "PEOPLE", actorId: await selectedActor(page, target) },
    inputs: {},
  });
  const current = await request(page, "case.get", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
  });
  if (current.outcome.kind !== "SUCCEEDED") {
    throw new Error("Unavailable work must remain readable to its actual reader");
  }
  expect(current.outcome.data.revision).toBe(3);
  expect(current.outcome.data.work.responsible).toEqual({
    actorId,
    principalKind: "HUMAN",
    availability: "DISABLED",
    displayName: {
      kind: "NAMED",
      value: "Synthetic matrix account",
    },
  });
  expect(current.outcome.data.mine).toBe(false);
};
export type DesignDataset = {
  routes: ReadonlyArray<MatrixRoute>;
  businessDate: string;
  cookies: Awaited<ReturnType<BrowserContext["cookies"]>>;
  close: () => Promise<void>;
};
export const prepareDesignDataset = async (
  browser: Browser,
  baseURL: string,
): Promise<DesignDataset> => {
  const context = await browser.newContext({
    baseURL,
    ignoreHTTPSErrors: true,
  });
  const page = await context.newPage();
  let restore = () => Promise.resolve();
  try {
    await openAuthenticated(page);
    // This independent fixture client begins only after the app's bootstrap reads settle.
    await expect(page.locator('.worklist[aria-busy="false"]')).toBeVisible();
    await expect(page.locator('.summary-band[aria-busy="true"]')).toHaveCount(0);
    const stewardship = await ownerStewardship(page);
    ({ restore } = stewardship);
    await stewardship.enable();
    const contextReply = await request(page, "context", {});
    if (
      contextReply.outcome.kind !== "SUCCEEDED" ||
      contextReply.outcome.data.requester.kind !== "ACTIVE"
    ) {
      throw new Error("E2E_MATRIX_CONTEXT_REFUSED");
    }
    const marker = `DM-${randomUUID()}`;
    const cases = await seedCases(page, marker, contextReply.outcome.data.requester.businessDate);
    restore = await setOwnerCaseWork(
      page,
      contextReply.outcome.data.requester.actorId,
      cases.references.undecided,
      contextReply.outcome.data.requester.businessDate,
      restore,
    );
    await unavailableCaseWork(
      page,
      stewardship.inventory.issuer,
      cases.references.decided,
      contextReply.outcome.data.requester.businessDate,
    );
    const actorId = await registerActor(
      page,
      stewardship.inventory.issuer,
      cases.references.undecided,
    );
    return {
      routes: routes({
        ...cases,
        marker,
        actorId,
      }),
      businessDate: contextReply.outcome.data.requester.businessDate,
      cookies: await context.cookies(),
      close: async () => {
        try {
          await restore();
        } finally {
          await context.close();
        }
      },
    };
  } catch (error) {
    try {
      await restore();
    } finally {
      await context.close();
    }
    throw error;
  }
};
