import { act } from "@testing-library/react";
import { expect, it } from "vitest";
import {
  render,
  screen,
  waitFor,
  testWorkspaceIdentity,
} from "../../support/presentation-test-support";
import { recoveryRecord, testJournal } from "../../support/recovery.fixtures";
import { RecoveryProvider } from "../../../src/recovery/RecoveryContext";
import { RecoveryBanner } from "../../../src/views/RecoveryBanner";

it("keeps the scoped unresolved journal count visible until the last attempt is removed", async () => {
  const first = recoveryRecord();
  const second = recoveryRecord("10000000-0000-4000-8000-000000000003");
  await testJournal.put(first);
  await testJournal.put({ ...second, actorId: "10000000-0000-4000-8000-000000000099" });
  render(
    <RecoveryProvider identity={testWorkspaceIdentity}>
      <RecoveryBanner />
    </RecoveryProvider>,
  );
  expect(await screen.findByText("Unconfirmed changes: 1")).toBeVisible();
  expect(screen.getByRole("link", { name: "Check now" })).toHaveAttribute("href", "/recovery");
  expect(screen.queryByRole("button")).toBeNull();
  await act(async () => {
    await testJournal.put(second);
  });
  expect(await screen.findByText("Unconfirmed changes: 2")).toBeVisible();
  await act(async () => {
    await testJournal.remove(first);
  });
  expect(await screen.findByText("Unconfirmed changes: 1")).toBeVisible();
  await act(async () => {
    await testJournal.remove(second);
  });
  await waitFor(() => {
    expect(screen.queryByRole("complementary")).toBeNull();
  });
});
