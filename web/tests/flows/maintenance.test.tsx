import {
  installationActions,
  requesterLinks,
  activeRequester,
} from "../support/human-requester.fixtures";
import {
  caseSubject,
  caseDocumentInput,
  registrationInputs,
} from "../support/case-document.fixtures";
import { prefixedInputs } from "../support/submission-inputs";
import { operationAffordance } from "../support/affordances.fixtures";

import { afterEach, expect, it, vi } from "vitest";
import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import {
  render,
  screen,
  waitFor,
  testWorkspaceIdentity,
} from "../support/presentation-test-support";
import { RequesterContext } from "../../src/hooks/useRequesterContext";
import { OperationEditor } from "../../src/views/OperationEditor";
import { UnresolvedNotice } from "../../src/views/operation/UnresolvedNotice";
import { RecoveryProvider } from "../../src/recovery/RecoveryContext";

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

const current = caseDocumentInput({
  subject: { ...caseSubject, revision: 1 },
  actions: [
    operationAffordance(
      "case.amendRegistration",
      {
        kind: "CASE",
        target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
        expectedRevision: 1,
      },
      { ...prefixedInputs("field:", fields.registration ?? {}) },
      [...registrationInputs],
      {},
    ),
  ],
  history: "NONE",
});
const paused = caseDocumentInput({
  subject: { ...caseSubject, caseId: current.subject.caseId, revision: 1 },
  actions: [],
  history: "NONE",
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
        initialAct="case.amendRegistration"
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
  expect({
    task: preview.mock.calls[0]![0].input.task,
    inputs: preview.mock.calls[0]![0].input.inputs,
  }).toEqual({
    task: "case.amendRegistration",
    inputs: { "field:claimantName": "Retained local draft" },
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
        actions: installationActions([]),
        links: requesterLinks(["OPERATIONS"], []),
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
      <RecoveryProvider identity={testWorkspaceIdentity}>
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
