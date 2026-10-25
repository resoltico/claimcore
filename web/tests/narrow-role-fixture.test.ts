import { expect, it, vi, beforeEach } from "vitest";

vi.mock("../e2e/session-helpers", () => ({ openAuthenticated: vi.fn() }));
vi.mock("../e2e/case-workflow", () => ({
  openCase: vi.fn(),
}));
vi.mock("../e2e/authority-setup", () => ({
  applyAuthority: vi.fn(),
  syntheticPrincipals: vi.fn(),
}));
import { applyAuthority } from "../e2e/authority-setup";
import { configureRole, type RoleFixture } from "../e2e/narrow-role-fixture";
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
beforeEach(() => vi.mocked(applyAuthority).mockReset());

it("restores only the confirmed baseline grant when narrow setup fails [CC-AUTH-001]", async () => {
  const failed = new Error("Synthetic grant response unavailable");
  let registered = false;
  vi.mocked(applyAuthority)
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
  const bodies = vi.mocked(applyAuthority).mock.calls.map((call) => call[1].act);
  expect(bodies).toMatchObject([
    { role: "DATA_STEWARD", kind: "REVOKE" },
    {
      role: "CASE_READER",
      kind: "GRANT",
      scope: { kind: "CASE", caseReference: fixture.reference },
    },
    { role: "DATA_STEWARD", kind: "GRANT" },
  ]);
  expect(bodies.some((body) => body.kind === "REVOKE" && body.role === "CASE_READER")).toBe(false);
});

it("does not invent rollback after the first authority action is unconfirmed [CC-AUTH-001]", async () => {
  const failed = new Error("Synthetic initial transition unconfirmed");
  vi.mocked(applyAuthority).mockRejectedValueOnce(failed);
  let restore: () => Promise<void> = () => Promise.resolve();
  await expect(
    configureRole(page, fixture, "CASE_READER", false, (cleanup) => {
      restore = cleanup;
    }),
  ).rejects.toBe(failed);
  await restore();
  expect(applyAuthority).toHaveBeenCalledOnce();
});
