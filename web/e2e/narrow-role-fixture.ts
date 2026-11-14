import { selectedActor, applyPeople, syntheticPrincipals } from "./people-setup";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { goToView } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import type { Page } from "@playwright/test";

import { openCase } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";

export type NarrowRole = "CASE_READER" | "CASE_HANDLER" | "DATA_STEWARD";
export type RoleFixture = Awaited<ReturnType<typeof prepareRoleFixture>>;

export const prepareRoleFixture = async (page: Page) => {
  await openAuthenticated(page);
  const reference = `NARROW-${randomUUID()}`;
  const unrelated = `UNRELATED-${randomUUID()}`;
  await openCase(page, unrelated);
  await goToView(page, "cases");
  await openCase(page, reference);
  const inventory = await syntheticPrincipals();
  const principal = {
    kind: "HUMAN",
    issuer: inventory.issuer,
    subject: inventory.stewardSubject,
  } as const;
  return { reference, unrelated, principal };
};

const setGrant = async (
  page: Page,
  fixture: RoleFixture,
  role: NarrowRole,
  scoped: boolean,
  active: boolean,
) =>
  applyPeople(page, {
    operationId: mintOperationId(),
    task: active ? "people.grant" : "people.revoke",
    binding: { kind: "PEOPLE", actorId: await selectedActor(page, fixture.principal) },
    inputs: {
      "input:role": role,
      "input:scope": scoped ? "CASE" : "INSTALLATION",
      ...(scoped ? { "field:caseReference": fixture.reference } : {}),
    },
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
