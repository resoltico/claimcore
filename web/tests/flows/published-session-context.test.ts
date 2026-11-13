// @vitest-environment node
import { expect, it } from "vitest";
import "../../../contracts/client";
import { isActiveOwnerContext } from "../../e2e/published-session-context.mjs";
import { actorContextInput } from "../support/inputs";
const active = actorContextInput({
  capabilities: ["MANAGE_ACCESS"],
  productVersion: "synthetic",
  businessDate: "2026-11-05",
  timeZone: "Etc/UTC",
  views: ["PEOPLE"],
  offers: [],
});
const reply = (requester: unknown) => ({
  representation: "DATA",
  outcome: {
    kind: "SUCCEEDED",
    data: {
      requester,
    },
  },
});
it("admits the current ACTIVE owner context rather than a retired flat shape", () => {
  expect(isActiveOwnerContext(reply(active))).toBe(true);
  expect(
    isActiveOwnerContext({
      outcome: {
        view: {
          regions: [],
        },
        kind: "SUCCEEDED",
        data: active,
      },
    }),
  ).toBe(false);
});
it.each([
  {
    ...active,
    capabilities: [],
  },
  {
    kind: "DISABLED",
    self: active.self,
  },
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
])("refuses non-owner requester state %# without inferring people", (requester) => {
  expect(isActiveOwnerContext(reply(requester))).toBe(false);
});
it.each([
  null,
  {},
  reply({
    kind: "ACTIVE",
    capabilities: ["MANAGE_ACCESS"],
  }),
  {
    outcome: {
      kind: "REFUSED",
      data: {
        code: "RESOURCE_UNAVAILABLE",
      },
    },
  },
])("refuses malformed or non-success context %#", (value) => {
  expect(isActiveOwnerContext(value)).toBe(false);
});
