import { randomUUID } from "node:crypto";
import { registration as registrationInput } from "../tests/support/inputs";
import type { Browser, BrowserContext, Page } from "@playwright/test";
import { expect } from "@playwright/test";
import {
  endpoints,
  type EndpointId,
  type RequestByEndpoint,
  type ResponseByEndpoint,
  type CaseAct,
} from "@contracts/client";
import { isResponse } from "../src/protocol/client";
import { casePath, actPath } from "../src/routing/routes";
import { applyAuthority, syntheticPrincipals } from "./authority-setup";
import { browserRequest, openAuthenticated, sessionToken } from "./session-helpers";

const request = async <K extends EndpointId>(
  page: Page,
  endpoint: K,
  body: RequestByEndpoint[K],
): Promise<ResponseByEndpoint[K]> => {
  const metadata = endpoints.find((entry) => entry.id === endpoint)!;
  const reply = await browserRequest(page, metadata.path, {
    method: metadata.method,
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify(body),
  });
  if (reply.status !== 200 || !isResponse(endpoint, reply.payload)) {
    throw new Error(`E2E_MATRIX_SEED_${endpoint}_REPLY`);
  }
  return reply.payload;
};
const execute = async (page: Page, body: RequestByEndpoint["operation.execute"]) => {
  const reply = await request(page, "operation.execute", body);
  if (reply.outcome.kind !== "ACCEPTED") {
    throw new Error(`E2E_MATRIX_SEED_${reply.outcome.kind}`);
  }
  return reply.outcome.data;
};
const caseAct = (page: Page, reference: string, revision: number, act: CaseAct) =>
  execute(page, {
    kind: "CASE",
    operationId: randomUUID(),
    caseReference: reference,
    expectedRevision: revision,
    act,
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
};
const views = [
  {
    id: "open-undecided",
    input: { lifecycle: ["ACTIVE"], status: ["OPENED"], progress: ["UNDECIDED"] },
  },
  { id: "decided-unpaid", input: { lifecycle: ["ACTIVE"], progress: ["DECIDED"] } },
  { id: "recent", input: { order: "RECENT" } },
  { id: "closed", input: { status: ["CLOSED"] } },
  { id: "voided", input: { lifecycle: ["VOIDED"] } },
  { id: "all", input: {} },
] as const;
const worklistRoutes = (marker: string): MatrixRoute[] => [
  ...views.map((view) => ({
    id: `cases-${view.id}`,
    path: `/cases?view=${view.id}`,
    ready: "#case-list-title",
    endpoint: "case.list" as const,
    expectedInput: { limit: 50, ...view.input },
  })),
  {
    id: "cases-filtered",
    path: `/cases?${new URLSearchParams({ lifecycle: "ACTIVE", status: "OPENED", progress: "UNDECIDED", order: "RECENT", q: marker })}`,
    ready: "#case-list-title",
    endpoint: "case.list",
    expectedInput: {
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
    path: casePath(references[state]),
    ready: "#case-detail-title",
    endpoint: "case.get" as const,
    expectedInput: { caseReference: references[state] },
  })),
  { id: "editor-open", path: "/cases/new", ready: "#operation-title" },
  ...(["DECIDE", "CORRECT", "VOID"] as const).map((act) => ({
    id: `editor-${act.toLowerCase()}`,
    path: actPath(act === "CORRECT" ? references.paid : references.undecided, act),
    ready: "#operation-title",
    endpoint: "case.get" as const,
  })),
];
const routes = (data: {
  references: Record<CaseState, string>;
  operationId: string;
  actorId: string;
  marker: string;
}): MatrixRoute[] => [
  ...worklistRoutes(data.marker),
  ...caseRoutes(data.references),
  {
    id: "operation-result",
    path: `/operations/${encodeURIComponent(data.operationId)}`,
    ready: "#operation-lookup-title",
    endpoint: "operation.observe",
  },
  {
    id: "authority-list",
    path: "/authority",
    ready: "#authority-title",
    endpoint: "authority.list",
  },
  {
    id: "authority-detail",
    path: `/authority/${encodeURIComponent(data.actorId)}`,
    ready: "#actor-grants-title",
    endpoint: "authority.grants",
  },
];
const findOwner = async (page: Page, target: { issuer: string; subject: string }) => {
  let cursor: string | null = null;
  do {
    const listing: ResponseByEndpoint["authority.list"] = await request(page, "authority.list", {
      limit: 50,
      ...(cursor === null ? {} : { cursor }),
    });
    if (listing.outcome.kind !== "SUCCEEDED") {
      throw new Error("E2E_MATRIX_AUTHORITY_LIST");
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
  const grants = await request(page, "authority.grants", { actorId, limit: 50 });
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
  const existing = await hasStewardship(page, owner.actorId);
  const set = (active: boolean) =>
    applyAuthority(page, {
      kind: "AUTHORITY",
      operationId: randomUUID(),
      target,
      act: {
        kind: active ? "GRANT" : "REVOKE",
        role: "DATA_STEWARD",
        scope: { kind: "INSTALLATION" },
      },
    });
  return {
    inventory,
    enable: async () => {
      if (!existing) {
        await set(true);
      }
    },
    restore: async () => {
      if (!existing && (await hasStewardship(page, owner.actorId))) {
        await set(false);
      }
    },
  };
};
const advanceCase = async (page: Page, reference: string, state: CaseState, date: string) => {
  if (state === "decided" || state === "paid") {
    await caseAct(page, reference, 1, {
      kind: "DECIDE",
      values: { paymentDecisionDate: date, payableAmount: "300.25", payableCurrency: "EUR" },
    });
  }
  if (state === "paid") {
    return (
      await caseAct(page, reference, 2, { kind: "RECORD_PAYMENT", values: { paymentDate: date } })
    ).operationId;
  }
  if (state === "closed") {
    await caseAct(page, reference, 1, { kind: "CLOSE" });
  }
  if (state === "voided") {
    await caseAct(page, reference, 1, { kind: "VOID", reason: "Synthetic matrix entry error" });
  }
  if (state === "erased") {
    await caseAct(page, reference, 1, { kind: "ERASE" });
  }
  return null;
};
const assertCase = async (page: Page, reference: string, state: CaseState) => {
  const read = await request(page, "case.get", { caseReference: reference });
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
    expect(value.state.fields.decision !== null).toBe(state === "decided" || state === "paid");
    expect(value.state.fields.payment !== null).toBe(state === "paid");
    if (state === "paid") {
      expect(value.advice.acts).toContain("CORRECT");
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
    await caseAct(page, reference, 0, { kind: "OPEN", values: registration });
    const acceptedId = await advanceCase(page, reference, state, date);
    if (acceptedId !== null) {
      operationId = acceptedId;
    }
    await assertCase(page, reference, state);
  }
  return { references, operationId };
};
const registerActor = async (page: Page, issuer: string, reference: string) => {
  const target = { kind: "SERVICE", issuer, subject: `matrix-${randomUUID()}` } as const;
  const registered = await execute(page, {
    kind: "AUTHORITY",
    operationId: randomUUID(),
    target,
    act: { kind: "REGISTER" },
  });
  if (registered.subject.kind !== "AUTHORITY") {
    throw new Error("E2E_MATRIX_AUTHORITY_RECEIPT");
  }
  await applyAuthority(page, {
    kind: "AUTHORITY",
    operationId: randomUUID(),
    target,
    act: { kind: "GRANT", role: "CASE_READER", scope: { kind: "CASE", caseReference: reference } },
  });
  return registered.subject.targetActorId;
};
export type DesignDataset = {
  routes: ReadonlyArray<MatrixRoute>;
  cookies: Awaited<ReturnType<BrowserContext["cookies"]>>;
  close: () => Promise<void>;
};
export const prepareDesignDataset = async (
  browser: Browser,
  baseURL: string,
): Promise<DesignDataset> => {
  const context = await browser.newContext({ baseURL, ignoreHTTPSErrors: true });
  const page = await context.newPage();
  let restore = () => Promise.resolve();
  try {
    await openAuthenticated(page);
    const stewardship = await ownerStewardship(page);
    ({ restore } = stewardship);
    await stewardship.enable();
    const contextReply = await request(page, "context", {});
    if (contextReply.outcome.kind !== "SUCCEEDED") {
      throw new Error("E2E_MATRIX_CONTEXT_REFUSED");
    }
    const marker = `DM-${randomUUID()}`;
    const cases = await seedCases(page, marker, contextReply.outcome.data.businessDate);
    const actorId = await registerActor(
      page,
      stewardship.inventory.issuer,
      cases.references.undecided,
    );
    return {
      routes: routes({ ...cases, marker, actorId }),
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
