import { expect, it } from "vitest";
import type { CaseSelector } from "@contracts/client";
import { coreLinkPath } from "../../../src/routing/coreLinks";
import { parseRoute } from "../../../src/routing/routes";
it("preserves exact reminders and ID versus reference meaning, including UUID-shaped references", () => {
  const id = "00000000-0000-4000-8000-000000000001";
  const targets: CaseSelector[] = [
    { kind: "ID", caseId: id },
    { kind: "REFERENCE", caseReference: id },
    { kind: "REFERENCE", caseReference: "CASE Ω" },
  ];
  for (const target of targets) {
    const first = coreLinkPath({
      endpoint: "reminder.get",
      input: { target, reminderId: "first" },
    })!;
    const second = coreLinkPath({
      endpoint: "reminder.get",
      input: { target, reminderId: "second" },
    })!;
    expect(first).not.toBe(second);
    const url = new URL(first, "https://claimcore.example");
    expect(parseRoute(url)).toEqual({ screen: "case", target, reminderId: "first" });
  }
});

it("maps every reachable standalone Core destination and preserves its selection and filters", () => {
  const actorId = "00000000-0000-4000-8000-000000000001";
  expect(
    coreLinkPath({
      endpoint: "case.get",
      input: { target: { kind: "REFERENCE", caseReference: "A B" } },
    }),
  ).toBe("/cases/reference/A%20B");
  expect(coreLinkPath({ endpoint: "people.grants", input: { actorId, limit: 20 } })).toBe(
    `/people/${actorId}`,
  );
  expect(coreLinkPath({ endpoint: "people.history", input: { actorId, limit: 20 } })).toBe(
    `/people-history?actorId=${actorId}`,
  );
  expect(coreLinkPath({ endpoint: "people.history", input: { limit: 20 } })).toBe(
    "/people-history",
  );
  expect(coreLinkPath({ endpoint: "people.list", input: { limit: 20 } })).toBe("/people");
  expect(coreLinkPath({ endpoint: "people.requests", input: { limit: 20 } })).toBe("/people");
  expect(coreLinkPath({ endpoint: "operation.get", input: { operationId: actorId } })).toBe(
    `/recovery/${actorId}`,
  );
  expect(coreLinkPath({ endpoint: "operation.get", input: {} })).toBe("/recovery/");
  expect(
    coreLinkPath({
      endpoint: "case.list",
      input: {
        limit: 20,
        view: "ALL",
        lifecycle: ["ACTIVE"],
        status: ["OPENED"],
        progress: ["UNDECIDED"],
        text: "A B",
        order: "RECENT",
        assignment: "MINE",
        responsibleActor: actorId,
        due: "TODAY",
      },
    }),
  ).toBe(
    `/cases?view=ALL&status=OPENED&progress=UNDECIDED&lifecycle=ACTIVE&q=A+B&order=RECENT&assignment=MINE&responsibleActor=${actorId}&due=TODAY`,
  );
  expect(
    coreLinkPath({
      endpoint: "reminder.list",
      input: {
        limit: 20,
        view: "COMPLETED",
        state: "COMPLETED",
        due: "TODAY",
        assignment: "MINE",
        text: "A B",
        includeInactive: true,
      },
    }),
  ).toBe(
    "/reminders?view=COMPLETED&state=COMPLETED&due=TODAY&assignment=MINE&q=A+B&includeInactive=true",
  );
});

it("leaves embedded-only reads without a fabricated standalone destination", () => {
  const target: CaseSelector = { kind: "REFERENCE", caseReference: "A" };
  expect(coreLinkPath({ endpoint: "reminder.list", input: { target, limit: 20 } })).toBe(
    "/reminders?caseReference=A&filters=manual",
  );
  // These queries belong to their parent workspace or discovery, not independent screens.
  const embedded = [
    { endpoint: "context", input: {} },
    { endpoint: "work.views", input: {} },
    { endpoint: "case.assignees", input: { target, limit: 20 } },
    { endpoint: "case.summary", input: {} },
    { endpoint: "case.timeline", input: { target } },
    { endpoint: "case.notes", input: { target, limit: 20 } },
    { endpoint: "case.payments", input: { target } },
  ] as const;
  for (const link of embedded) {
    expect(coreLinkPath(link)).toBeUndefined();
  }
});

it.each([undefined, false, true] as const)(
  "preserves the supplied Core view's inactive override %s",
  (includeInactive) => {
    const input = {
      view: "COMPLETED",
      ...(includeInactive === undefined ? {} : { includeInactive }),
      limit: 50,
    };
    const path = coreLinkPath({ endpoint: "reminder.list", input });
    expect(path).toBe(
      `/reminders?view=COMPLETED${includeInactive === undefined ? "" : `&includeInactive=${String(includeInactive)}`}`,
    );
  },
);
