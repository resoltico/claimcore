import { randomUUID } from "node:crypto";
import type { Page } from "@playwright/test";
import { applyAuthority, syntheticPrincipals } from "./authority-setup";
import { openCase, startCommand, prepare, keepForRecovery } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";

export type NarrowRole =
  "CASE_READER" | "CASE_EDITOR" | "RECOVERY_OPERATOR" | "RECOVERY_EXPORTER" | "DATA_STEWARD";
export type RoleFixture = Awaited<ReturnType<typeof prepareRoleFixture>>;

export const prepareRoleFixture = async (page: Page) => {
  await openAuthenticated(page);
  const reference = `NARROW-${randomUUID()}`;
  const unrelated = `UNRELATED-${randomUUID()}`;
  await openCase(page, unrelated);
  await page.getByRole("button", { name: "Cases", exact: true }).click();
  await openCase(page, reference);
  await startCommand(page, "Close the case");
  const identity = await prepare(page);
  await keepForRecovery(page);
  const inventory = await syntheticPrincipals();
  const principal = { kind: "HUMAN", issuer: inventory.issuer, subject: inventory.stewardSubject };
  return { reference, unrelated, identity, principal };
};

const setGrant = (
  page: Page,
  fixture: RoleFixture,
  role: NarrowRole,
  scoped: boolean,
  active: boolean,
) =>
  applyAuthority(page, "authority.setGrant", {
    eventId: randomUUID(),
    principal: fixture.principal,
    role,
    scope: scoped ? { kind: "CASE", caseReference: fixture.reference } : { kind: "INSTALLATION" },
    active,
  });

export const configureRole = async (
  page: Page,
  fixture: RoleFixture,
  role: NarrowRole | null,
  scoped: boolean,
  registerRestore: (restore: () => Promise<void>) => void,
) => {
  let baselineRevoked = false;
  let roleGranted = false;
  registerRestore(async () => {
    if (roleGranted && role !== null) {
      await setGrant(page, fixture, role, scoped, false);
      roleGranted = false;
    }
    if (baselineRevoked) {
      await setGrant(page, fixture, "DATA_STEWARD", false, true);
      baselineRevoked = false;
    }
  });
  if (role === "DATA_STEWARD") {
    return;
  }
  await setGrant(page, fixture, "DATA_STEWARD", false, false);
  baselineRevoked = true;
  if (role !== null) {
    await setGrant(page, fixture, role, scoped, true);
    roleGranted = true;
  }
};
