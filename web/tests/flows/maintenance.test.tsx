import { afterEach, expect, it, vi } from "vitest";
import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { RequesterContext } from "../../src/hooks/useRequesterContext";
import { OperationEditor } from "../../src/views/OperationEditor";
import { UnresolvedNotice } from "../../src/views/operation/UnresolvedNotice";
import { RecoveryProvider } from "../../src/recovery/RecoveryContext";
import { activeRequester } from "../support/human-requester.fixtures";
import { currentCaseInput } from "../support/inputs";
import { caseAdvice } from "../support/case-advice.fixtures";
import { fields } from "../support/service-ui.fixtures";
import { DisclosureRefreshDriver } from "../support/people-drawer.fixtures";
import { pendingLookup } from "../support/operation-lookup.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { productMetadata } from "../../src/protocol/productMetadata";
import { changeField } from "../support/field-entry";
import { service } from "../../src/api/service";
import {
  clearRecoveryFixtures,
  recoveryRecord,
  recoveryScope,
  testJournal,
} from "../support/recovery.fixtures";

const current = currentCaseInput({
  caseReference: fields.caseReference,
  revision: 1,
  state: { kind: "ACTIVE", fields },
  advice: { ...caseAdvice(["AMEND_REGISTRATION"]), history: "NONE" },
});
const paused = currentCaseInput({
  caseId: current.context.caseId,
  caseReference: fields.caseReference,
  revision: 1,
  state: { kind: "ACTIVE", fields },
  advice: { ...caseAdvice([]), history: "NONE" },
});
afterEach(() => {
  vi.restoreAllMocks();
  clearRecoveryFixtures();
});

it("keeps an authored draft through Core offer withdrawal and explicit resumption without sending it", async () => {
  const preview = vi.spyOn(service, "preview").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
  });
  const read = mockEndpoint("case.get")
    .mockResolvedValueOnce({
      kind: "outcome",
      status: 200,
      value: { representation: "HUMAN", outcome: { kind: "SUCCEEDED", document: paused } },
    })
    .mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: { representation: "HUMAN", outcome: { kind: "SUCCEEDED", document: current } },
    });
  let invalidate: (() => void) | undefined;
  render(
    <>
      <DisclosureRefreshDriver
        capture={(next) => {
          invalidate = next;
        }}
      />
      <OperationEditor
        definition={productMetadata}
        current={current}
        initialAct="AMEND_REGISTRATION"
        onClose={vi.fn()}
        onCommitted={vi.fn()}
        onMutationLockChange={vi.fn()}
      />
    </>,
  );
  const user = userEvent.setup();
  await user.click(screen.getByRole("checkbox", { name: "Change Claimant name" }));
  changeField("Claimant name", "Retained local draft");
  act(() => {
    invalidate!();
  });
  await user.click(await screen.findByRole("button", { name: "Reload current case" }));
  await waitFor(() => {
    expect(read).toHaveBeenCalledOnce();
  });
  expect(screen.queryByRole("textbox", { name: "Claimant name" })).toBeNull();
  expect(screen.getByRole("button", { name: "Review changes" })).toBeDisabled();
  expect(preview).not.toHaveBeenCalled();
  act(() => {
    invalidate!();
  });
  await user.click(await screen.findByRole("button", { name: "Reload current case" }));
  expect(await screen.findByRole("textbox", { name: "Claimant name" })).toHaveValue(
    "Retained local draft",
  );
  expect(preview).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(preview).toHaveBeenCalledOnce();
  });
  expect(preview.mock.calls[0]![0].input.act).toEqual({
    kind: "AMEND_REGISTRATION",
    values: { claimantName: "Retained local draft" },
  });
});

it("automatically checks unconfirmed identity when Core has withdrawn write tasks for maintenance", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const lookup = vi
    .spyOn(service, "operationGet")
    .mockResolvedValue(pendingLookup(record.operationId));
  render(
    <RequesterContext
      value={{
        ...activeRequester,
        context: { ...activeRequester.context, tasks: [], views: ["OPERATIONS"] },
        screen: {
          regions: [
            {
              role: "NOTICE",
              content: {
                kind: "NOTICE",
                messageId: "diagnostic.REGISTER_MAINTENANCE",
                tone: "WARNING",
              },
            },
          ],
        },
      }}
    >
      <RecoveryProvider>
        <UnresolvedNotice showEmpty />
      </RecoveryProvider>
    </RequesterContext>,
  );
  await screen.findByText("Checking whether the change was recorded. Final answer by:");
  expect(document.querySelector('time[datetime="2026-11-07T10:10:00.000000Z"]')).not.toBeNull();
  await waitFor(() => {
    expect(lookup).toHaveBeenCalledOnce();
  });
  expect(screen.getByRole("button", { name: "Check now" })).not.toBeDisabled();
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
});
