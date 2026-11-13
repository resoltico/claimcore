// @vitest-environment node
import { expect, it, vi, beforeEach } from "vitest";

vi.mock("../../e2e/session-helpers", () => ({ openAuthenticated: vi.fn() }));
vi.mock("../../e2e/case-workflow", () => ({
  openCase: vi.fn(),
}));
vi.mock("../../e2e/people-setup", () => ({
  applyPeople: vi.fn(),
  syntheticPrincipals: vi.fn(),
}));
import { applyPeople } from "../../e2e/people-setup";
import { configureRole, type RoleFixture } from "../../e2e/narrow-role-fixture";
import type { Page } from "@playwright/test";

const fixture: RoleFixture = {
  reference: "SYNTHETIC-NARROW",
  unrelated: "SYNTHETIC-UNRELATED",
  principal: {
    kind: "HUMAN",
    issuer: "https://identity.example.test",
    subject: "synthetic-steward",
  },
};
const page = {} as Page;
beforeEach(() => vi.mocked(applyPeople).mockReset());

it("restores only the confirmed baseline grant when narrow setup fails", async () => {
  const failed = new Error("Synthetic grant response unavailable");
  let registered = false;
  vi.mocked(applyPeople)
    .mockImplementationOnce(() => {
      expect(registered).toBe(true);
      return Promise.resolve();
    })
    .mockRejectedValueOnce(failed)
    .mockResolvedValueOnce();
  let restore: () => Promise<void> = () => Promise.resolve();
  await expect(
    configureRole(page, fixture, "CASE_READER", true, (cleanup) => {
      registered = true;
      restore = cleanup;
    }),
  ).rejects.toBe(failed);
  await restore();
  const bodies = vi.mocked(applyPeople).mock.calls.map((call) => call[1].act);
  expect(bodies).toMatchObject([
    { role: "DATA_STEWARD", kind: "people.revoke" },
    {
      role: "CASE_READER",
      kind: "people.grant",
      scope: { kind: "CASE", target: { kind: "REFERENCE", caseReference: fixture.reference } },
    },
    { role: "DATA_STEWARD", kind: "people.grant" },
  ]);
  expect(bodies.some((body) => body.kind === "people.revoke" && body.role === "CASE_READER")).toBe(
    false,
  );
});

it("does not invent rollback after the first people action is unconfirmed", async () => {
  const failed = new Error("Synthetic initial transition unconfirmed");
  vi.mocked(applyPeople).mockRejectedValueOnce(failed);
  let restore: () => Promise<void> = () => Promise.resolve();
  await expect(
    configureRole(page, fixture, "CASE_READER", false, (cleanup) => {
      restore = cleanup;
    }),
  ).rejects.toBe(failed);
  await restore();
  expect(applyPeople).toHaveBeenCalledOnce();
});
