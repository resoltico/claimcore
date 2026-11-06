// @vitest-environment node
import { expect, it } from "vitest";
import { isResponse } from "../../../contracts/client";
import { isActiveOwnerContext } from "../../e2e/published-session-context.mjs";
import { actorContextInput } from "../support/inputs";
const active = actorContextInput({
  capabilities: ["MANAGE_AUTHORITY"],
  productVersion: "synthetic",
  businessDate: "2026-11-05",
  timeZone: "Etc/UTC",
  views: ["AUTHORITY"],
  offers: [],
});
const reply = (requester: unknown) => ({ outcome: { kind: "SUCCEEDED", data: { requester } } });
it("admits the current ACTIVE owner context rather than a retired flat shape", () => {
  expect(isResponse("context", reply(active))).toBe(true);
  expect(isActiveOwnerContext(reply(active))).toBe(true);
  expect(isActiveOwnerContext({ outcome: { kind: "SUCCEEDED", data: active } })).toBe(false);
});
it.each([
  { ...active, capabilities: [] },
  { kind: "DISABLED", self: active.self },
  {
    kind: "NO_ROLES",
    self: active.self,
    installationId: active.installationId,
    businessDate: active.businessDate,
    timeZone: active.timeZone,
  },
  {
    kind: "NOT_REGISTERED",
    issuer: active.issuer,
    subject: "unknown",
    claims: {},
  },
])("refuses non-owner requester state %# without inferring authority", (requester) => {
  expect(isResponse("context", reply(requester))).toBe(true);
  expect(isActiveOwnerContext(reply(requester))).toBe(false);
});
it.each([
  null,
  {},
  reply({ kind: "ACTIVE", capabilities: ["MANAGE_AUTHORITY"] }),
  { outcome: { kind: "REFUSED", data: { code: "RESOURCE_UNAVAILABLE" } } },
])("refuses malformed or non-success context %#", (value) => {
  expect(isActiveOwnerContext(value)).toBe(false);
});
