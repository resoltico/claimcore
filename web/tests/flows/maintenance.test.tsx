import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { RegisterMaintenance } from "@contracts/client";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { RequesterContext } from "../../src/hooks/useRequesterContext";
import { OperationEditor } from "../../src/views/OperationEditor";
import { UnresolvedNotice } from "../../src/views/operation/UnresolvedNotice";
import { RecoveryProvider } from "../../src/recovery/RecoveryContext";
import { context as requester } from "../support/service-ui.fixtures";
import { current } from "../support/operation-form.fixtures";
import { productMetadata } from "../../src/protocol/productMetadata";
import { changeField } from "../support/field-entry";
import { service } from "../../src/api/service";
import {
  clearRecoveryFixtures,
  recoveryRecord,
  recoveryScope,
  testJournal,
} from "../support/recovery.fixtures";

afterEach(() => {
  vi.restoreAllMocks();
  clearRecoveryFixtures();
});
const editor = (maintenance: RegisterMaintenance) => (
  <RequesterContext value={{ ...requester, maintenance }}>
    <OperationEditor
      definition={productMetadata}
      current={current}
      initialAct="AMEND_REGISTRATION"
      onClose={vi.fn()}
      onCommitted={vi.fn()}
      onMutationLockChange={vi.fn()}
    />
  </RequesterContext>
);

it("keeps an authored draft through maintenance entry and explicit resumption without sending it", async () => {
  const preview = vi
    .spyOn(service, "preview")
    .mockResolvedValue({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    });
  const view = render(editor({ active: false, generation: 0 }));
  await userEvent.setup().click(screen.getByRole("checkbox", { name: "Change Claimant name" }));
  changeField("Claimant name", "Retained local draft");
  view.rerender(editor({ active: true, generation: 1 }));
  expect(screen.getByRole("textbox", { name: "Claimant name" })).toHaveValue(
    "Retained local draft",
  );
  expect(screen.getByRole("button", { name: "Review changes" })).toBeDisabled();
  expect(preview).not.toHaveBeenCalled();
  view.rerender(editor({ active: false, generation: 1 }));
  expect(screen.getByRole("textbox", { name: "Claimant name" })).toHaveValue(
    "Retained local draft",
  );
  expect(preview).not.toHaveBeenCalled();
  await userEvent.setup().click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(preview).toHaveBeenCalledTimes(1);
  });
  expect(preview.mock.calls[0]![0].input.act).toEqual({
    kind: "AMEND_REGISTRATION",
    values: { claimantName: "Retained local draft" },
  });
});

it("keeps passive recovery usable and its identity retained while settlement is paused", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const status = vi
    .spyOn(service, "status")
    .mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: { operationId: record.operationId, outcome: { kind: "UNRESOLVED", data: null } },
    });
  const settle = vi.spyOn(service, "resolve");
  render(
    <RequesterContext value={{ ...requester, maintenance: { active: true, generation: 1 } }}>
      <RecoveryProvider>
        <UnresolvedNotice showEmpty />
      </RecoveryProvider>
    </RequesterContext>,
  );
  const check = await screen.findByRole("button", { name: "Check operation status" });
  expect(check).not.toBeDisabled();
  expect(screen.getByRole("button", { name: "Settle operation permanently" })).toBeDisabled();
  await userEvent.setup().click(check);
  await waitFor(() => {
    expect(status).toHaveBeenCalledTimes(1);
  });
  await waitFor(() => {
    expect(check).not.toBeDisabled();
  });
  expect(settle).not.toHaveBeenCalled();
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
});
