import {
  caseSubject,
  caseDocumentInput,
  caseIdentityScreen,
} from "../support/case-document.fixtures";
import { operationAffordance } from "../support/affordances.fixtures";

import { requestAddress, endpointResponse } from "../support/transport-response.fixtures";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen, TestSession, waitFor } from "../support/presentation-test-support";
import { definition } from "../support/service-ui.fixtures";
import { WorkOperation } from "../../src/views/work/WorkOperation";
import { current } from "../support/presentation-state.fixtures";
import { CaseDetail } from "../../src/views/CaseDetail";

const readable = caseDocumentInput(
  {
    subject: { ...caseSubject, revision: 4 },
    actions: [
      operationAffordance(
        "work.update",
        {
          kind: "WORK",
          target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
          expectedRevision: caseSubject.workRevision,
        },
        {
          "field:handlingSummary": "Existing summary",
          "field:responsibleActor": "30000000-0000-4000-8000-000000000003",
        },
        ["field:handlingSummary", "field:responsibleActor"],
        {},
      ),
      operationAffordance("reminder.create", {
        kind: "REMINDER",
        target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
      }),
    ],
    history: "NONE",
  },
  {
    regions: [
      ...caseIdentityScreen.regions,
      {
        role: "WORK",
        titleId: "section.work.label",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "responsibleActor",
              labelId: "field.responsibleActor.label",
              prominence: "PRIMARY",
              value: { kind: "ABSENT", reasonId: "label.notRecorded" },
            },
            {
              key: "handlingSummary",
              labelId: "field.handlingSummary.label",
              prominence: "PRIMARY",
              value: { kind: "ABSENT", reasonId: "label.notRecorded" },
            },
          ],
        },
      },
      {
        role: "NEXT_ACTIONS",
        titleId: "label.nextActions",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "action",
              labelId: "field.reminderAction.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: [],
        },
      },
    ],
  },
);
const response = () =>
  endpointResponse("case.get", {
    representation: "HUMAN",
    outcome: { kind: "SUCCEEDED", document: readable },
  });
const detail = (credential: string) => (
  <TestSession credential={credential}>
    <CaseDetail
      target={{ kind: "ID", caseId: readable.subject.caseId }}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />
  </TestSession>
);
afterEach(() => vi.unstubAllGlobals());

const captureRequests = () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((url) =>
    requestAddress(url) === "/api/case.get"
      ? Promise.resolve(response())
      : new Promise<Response>(() => {
          /* Keep non-read requests pending to inspect authored bytes. */
        }),
  );
  vi.stubGlobal("fetch", fetch);
  const previews = () =>
    fetch.mock.calls.filter(([url]) => requestAddress(url) === "/api/operation.preview");
  const inputs = (): unknown => {
    const body = previews()[0]![1]?.body;
    if (typeof body !== "string") {
      throw new Error("Expected JSON request text");
    }
    const submission: unknown = JSON.parse(body);
    if (typeof submission !== "object" || submission === null || !("inputs" in submission)) {
      throw new Error("Expected operation inputs");
    }
    return submission.inputs;
  };
  return { previews, inputs };
};
it("summary editing acquires only the selected UPDATE field from realistic work seeds", async () => {
  const requests = captureRequests();
  const user = userEvent.setup();
  render(detail("same-owner"));
  await user.click(await screen.findByRole("button", { name: "Edit summary" }));
  const summary = screen.getByRole("textbox", { name: "Handling summary" });
  expect(summary).toHaveValue("Existing summary");
  expect(requests.previews()).toHaveLength(0);
  await user.clear(summary);
  await user.type(summary, "Edited summary");
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(requests.previews()).toHaveLength(1);
  });
  expect(requests.inputs()).toEqual({ "field:handlingSummary": "Edited summary" });
});
it("assignment acquires only the selected UPDATE field from realistic work seeds", async () => {
  const requests = captureRequests();
  const user = userEvent.setup();
  render(detail("same-owner"));
  await user.click(await screen.findByRole("button", { name: "Change" }));
  expect(requests.previews()).toHaveLength(0);
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(requests.previews()).toHaveLength(1);
  });
  expect(requests.inputs()).toEqual({
    "field:responsibleActor": "30000000-0000-4000-8000-000000000003",
  });
});

it("required reminder seeds reach automatic preview through the real coordinator", async () => {
  const requests = captureRequests();
  render(
    <TestSession credential="same-owner">
      <WorkOperation
        current={current}
        subject="REMINDER"
        kind="reminder.update"
        affordance={operationAffordance(
          "reminder.update",
          {
            kind: "REMINDER",
            target: { kind: "ID", caseId: current.subject.caseId },
            reminderId: "30000000-0000-4000-8000-000000000003",
            expectedRevision: 3,
          },
          { "input:reminderAction": "Call claimant", "input:reminderDueDate": "2026-11-20" },
        )}
        disclosureAvailable
        onClose={vi.fn()}
        onRecorded={vi.fn()}
        onLockChange={vi.fn()}
        onDisclosureRefused={vi.fn()}
      />
    </TestSession>,
  );
  await waitFor(() => {
    expect(requests.previews()).toHaveLength(1);
  });
  expect(requests.inputs()).toEqual({
    "input:reminderAction": "Call claimant",
    "input:reminderDueDate": "2026-11-20",
  });
});
