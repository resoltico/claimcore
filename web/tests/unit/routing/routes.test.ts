import { expect, it } from "vitest";
import { actPath, casePath, referencePath, parseRoute } from "../../../src/routing/routes";
import { navigate, acquireNavigationLock } from "../../../src/routing/navigation";
import { rememberLoginReturn, restoreLoginReturn } from "../../../src/routing/loginReturn";

it.each(["CASE-0042", "new", "NEW", ".", "..", "/", "%", "~", "~bmV3", "A/B", "100%", '!~<>"'])(
  "round trips valid reference %s through actual URL normalization",
  (reference) => {
    const location = new URL(referencePath(reference), window.location.origin);
    expect(parseRoute(location)).toEqual({
      screen: "case",
      target: { kind: "REFERENCE", caseReference: reference },
    });
    expect(
      parseRoute(
        new URL(
          actPath({ kind: "REFERENCE", caseReference: reference }, "case.correct"),
          window.location.origin,
        ),
      ),
    ).toEqual({
      screen: "caseAct",
      target: { kind: "REFERENCE", caseReference: reference },
      act: "case.correct",
    });
  },
);
it("reserves the OPEN route while preserving all ordinary deep links", () => {
  expect(parseRoute(new URL("/cases/new", window.location.origin))).toEqual({ screen: "newCase" });
  expect(parseRoute(new URL("/recovery/op-id", window.location.origin))).toEqual({
    screen: "recovery",
    operationId: "op-id",
  });
  expect(parseRoute(new URL("/people/actor-id", window.location.origin))).toEqual({
    screen: "people",
    actorId: "actor-id",
  });
  expect(parseRoute(new URL("/cases/%", window.location.origin))).toEqual({ screen: "missing" });
});
it("keeps worklist state in the URL and blocks mutation navigation and unload", () => {
  navigate("/cases?view=recent&q=R%C4%ABga&order=RECENT");
  expect(parseRoute(window.location)).toEqual({
    screen: "cases",
    query: "?view=recent&q=R%C4%ABga&order=RECENT",
  });
  const releaseLock = acquireNavigationLock();
  expect(navigate("/people")).toBe(false);
  const unload = new Event("beforeunload", { cancelable: true });
  window.dispatchEvent(unload);
  expect(unload.defaultPrevented).toBe(true);
  releaseLock();
});
it("restores a same-origin deep link once after login and refuses an external return", () => {
  navigate("/cases/reference/CASE-0042/acts/case.decide");
  rememberLoginReturn();
  navigate("/cases");
  restoreLoginReturn();
  expect(window.location.pathname).toBe("/cases/reference/CASE-0042/acts/case.decide");
  navigate("/cases");
  restoreLoginReturn();
  expect(window.location.pathname).toBe("/cases");
  sessionStorage.setItem("claimcore.loginReturn", "//outside.example/cases");
  restoreLoginReturn();
  expect(window.location.pathname).toBe("/cases");
  expect(sessionStorage.getItem("claimcore.loginReturn")).toBeNull();
});

it("keeps canonical case identity separate from a UUID-shaped reference", () => {
  const caseId = "20000000-0000-4000-8000-000000000001";
  expect(parseRoute(new URL(casePath(caseId), location.origin))).toEqual({
    screen: "case",
    target: { kind: "ID", caseId },
  });
  expect(parseRoute(new URL(referencePath(caseId), location.origin))).toEqual({
    screen: "case",
    target: { kind: "REFERENCE", caseReference: caseId },
  });
  for (const invalid of ["/cases/CASE-0042"]) {
    expect(parseRoute(new URL(invalid, location.origin))).toEqual({ screen: "missing" });
  }
});

it.each(["/work", "/authority", "/authority/actor-id", "/operations", "/operations/op-id"])(
  "rejects retired route %s without an alias",
  (path) => {
    expect(parseRoute(new URL(path, location.origin))).toEqual({ screen: "missing" });
  },
);
it("distinguishes default entry and the separate public areas", () => {
  expect(parseRoute(new URL("/", location.origin))).toEqual({ screen: "home" });
  expect(parseRoute(new URL("/reminders?view=PENDING", location.origin))).toEqual({
    screen: "reminders",
    query: "?view=PENDING",
  });
  expect(parseRoute(new URL("/people-history", location.origin))).toEqual({
    screen: "peopleHistory",
    actorId: "",
  });
  expect(parseRoute(new URL("/recovery", location.origin))).toEqual({
    screen: "recovery",
    operationId: "",
  });
});

it("preserves the explicit history actor instead of broadening to all actors", () => {
  const actorId = "10000000-0000-4000-8000-000000000001";
  expect(parseRoute(new URL(`/people-history?actorId=${actorId}`, location.origin))).toEqual({
    screen: "peopleHistory",
    actorId,
  });
});
it.each([
  "actorId=",
  "actorId=10000000-0000-4000-8000-000000000001&actorId=10000000-0000-4000-8000-000000000002",
  "actorId=10000000-0000-4000-8000-000000000001&actorId=10000000-0000-4000-8000-000000000001",
])("rejects a supplied ambiguous or invalid history actor: %s", (query) => {
  expect(parseRoute(new URL(`/people-history?${query}`, location.origin))).toEqual({
    screen: "missing",
  });
});

it.each(["not-a-uuid", "00000000-0000-0000-0000-000000000000"])(
  "passes supplied case identity to the core: %s",
  (caseId) => {
    expect(parseRoute(new URL(casePath(caseId), location.origin))).toEqual({
      screen: "case",
      target: { kind: "ID", caseId },
    });
  },
);
it.each(["not-an-actor", "\n10000000-0000-4000-8000-000000000001"])(
  "passes a supplied history actor to the core without broadening: %s",
  (actorId) => {
    expect(
      parseRoute(
        new URL(`/people-history?actorId=${encodeURIComponent(actorId)}`, location.origin),
      ),
    ).toEqual({
      screen: "peopleHistory",
      actorId,
    });
  },
);

const selectedCase = "20000000-0000-4000-8000-000000000001";
const firstRow = "20000000-0000-4000-8000-000000000002";
const secondRow = "20000000-0000-4000-8000-000000000003";
const target = { kind: "ID", caseId: selectedCase } as const;

it.each([
  ["party.update", "PARTY", "partyId"],
  ["case.removePayment", "CASE", "paymentId"],
] as const)("carries only the declared identity for %s", (act, kind, key) => {
  const path = actPath(target, act, {
    kind,
    target,
    expectedRevision: 7,
    erasureReviewToken: "synthetic-review",
    [key]: firstRow,
  });
  expect(path).toBe(`${casePath(selectedCase)}/acts/${act}?${key}=${firstRow}`);
  expect(parseRoute(new URL(path, location.origin))).toEqual({
    screen: "caseAct",
    target,
    act,
    selection: { kind: "selected", id: firstRow },
  });
});

it("keeps reviewed erasure facts out of the URL", () => {
  expect(
    actPath(target, "case.erase", {
      kind: "CASE",
      target,
      expectedRevision: 7,
      erasureReviewToken: "synthetic-review",
    }),
  ).toBe(`${casePath(selectedCase)}/acts/case.erase`);
});

it.each([
  ["party.remove", "PARTY", "partyId"],
  ["case.removePayment", "CASE", "paymentId"],
] as const)(
  "refuses conflicting selectors for %s without treating them as omitted",
  (act, kind, key) => {
    const path = actPath(target, act);
    const route = { screen: "caseAct", target, act };
    expect(parseRoute(new URL(path, location.origin))).toEqual(route);
    const withQuery = (query: URLSearchParams) =>
      parseRoute(new URL(`${path}?${query}`, location.origin));
    const rejected = [
      new URLSearchParams([
        [key, firstRow],
        [key, secondRow],
      ]),
      new URLSearchParams([
        [key, secondRow],
        [key, firstRow],
      ]),
      new URLSearchParams([
        [key, firstRow],
        [key, firstRow],
      ]),
      new URLSearchParams({
        binding: `{"kind":"${kind}","${key}":"${firstRow}","${key}":"${secondRow}"}`,
      }),
      new URLSearchParams({ binding: JSON.stringify({ kind, [key]: firstRow }) }),
      new URLSearchParams({ binding: JSON.stringify({ kind }) }),
      new URLSearchParams({ [key]: "" }),
      new URLSearchParams({ [key]: "not-an-id" }),
      new URLSearchParams({ [key]: "00000000-0000-0000-0000-000000000000" }),
      new URLSearchParams({ [key]: firstRow, expectedRevision: "1" }),
      new URLSearchParams({ [key]: firstRow, erasureReviewToken: "opaque" }),
      new URLSearchParams({ [key]: firstRow, unknown: "1" }),
    ];
    for (const query of rejected) {
      expect(withQuery(query)).toEqual({ ...route, selection: { kind: "invalid" } });
    }
    expect(parseRoute(new URL(actPath(target, act, { kind }), location.origin))).toEqual({
      ...route,
      selection: { kind: "invalid" },
    });
  },
);
