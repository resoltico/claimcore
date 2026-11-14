import { operationAffordance } from "../../../support/affordances.fixtures";
import type { RequestByEndpoint, HumanReview, HumanReceiptDocument } from "@contracts/client";
import { requestText, requestAddress } from "../../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { act, waitFor, screen, within } from "@testing-library/react";
import { useRef } from "react";
import { render } from "../../../support/presentation-test-support";
import { useOperation, type OperationModel } from "../../../../src/hooks/operation/useOperation";
import { OperationForm, OperationFormActions } from "../../../../src/views/operation/OperationForm";
import { OperationPanel } from "../../../../src/views/operation/OperationPanel";
import { OperationReview } from "../../../../src/views/operation/OperationReview";
import type { CurrentCase } from "../../../../src/protocol/types";
import type { RecoveryJournal, RecoveryRecord } from "../../../../src/protocol/recoveryJournal";
const caseId = "00000000-0000-4000-8000-000000000002";
const scope = {
  serviceOrigin: "https://claimcore.example",
  installationId: "00000000-0000-4000-8000-000000000003",
  actorId: "00000000-0000-4000-8000-000000000004",
};
const basis = {
  kind: "SNAPSHOT",
  observation: {
    observedAt: "2026-11-09T12:00:00.000000Z",
    businessDate: "2026-11-09",
    timeZone: "Etc/UTC",
  },
} as const;
const current: CurrentCase = {
  screen: {
    regions: [
      {
        role: "IDENTITY",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "opaque",
              labelId: "label.details",
              prominence: "PRIMARY",
              value: {
                kind: "TEXT",
                value: "Core opaque target Ω",
              },
            },
          ],
        },
      },
    ],
  },
  subject: { kind: "CASE", caseId, revision: 7, workRevision: 2 },
  actions: [
    operationAffordance(
      "case.close",
      { kind: "CASE", target: { kind: "ID", caseId }, expectedRevision: 7 },
      {},
      [],
      {},
    ),
  ],
  links: [],
  history: "NONE",
  nextCursor: null,
  basis,
  disclosure: "CURRENT",
};
const review: HumanReview = {
  presentation: {
    changes: [],
    unchanged: [],
  },
  document: {
    screen: {
      regions: [],
    },
    subject: {
      kind: "REVIEW",
      confirmation: {
        kind: "CASE_REFERENCE",
        reference: "CORE-CASE",
      },
      consequence: "ROUTINE",
    },
    actions: [
      operationAffordance(
        "case.close",
        { kind: "CASE", target: { kind: "ID", caseId }, expectedRevision: 7 },
        {},
        [],
      ),
    ],
    links: [],
    history: null,
    nextCursor: null,
    basis,
    disclosure: "CURRENT",
  },
};
const receipt: HumanReceiptDocument = {
  screen: {
    regions: [],
  },
  subject: { kind: "RECEIPT", operationId: "10000000-0000-7000-8000-000000000001" },
  actions: [],
  links: [
    {
      rel: "OPEN_CASE",
      labelId: "field.caseReference.label",
      link: { endpoint: "case.get", input: { target: { kind: "ID", caseId } } },
    },
    {
      rel: "FOLLOW_UP",
      labelId: "field.caseReference.label",
      link: { endpoint: "case.get", input: { target: { kind: "ID", caseId } } },
    },
  ],
  history: null,
  nextCursor: null,
  basis,
  disclosure: "CURRENT",
};
afterEach(() => vi.unstubAllGlobals());
const fixture = (lost = false, putFails = false, panel = false) => {
  const events: string[] = [];
  const bodies: string[] = [];
  const records: RecoveryRecord[] = [];
  const journal: RecoveryJournal = {
    list: vi.fn(() => Promise.try(() => records)),
    put: vi.fn((record: RecoveryRecord) =>
      Promise.try(() => {
        events.push("journal");
        if (putFails) {
          throw new Error("journal failure");
        }
        records.push(record);
      }),
    ),
    remove: vi.fn((record: RecoveryRecord) =>
      Promise.try(() => {
        events.push("cleanup");
        const index = records.indexOf(record);
        if (index >= 0) {
          records.splice(index, 1);
        }
      }),
    ),
  };
  let first = true;
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation((url, options) =>
      Promise.try(() => {
        const body = requestText(options?.body);
        const command: unknown = JSON.parse(body);
        bodies.push(body);
        const preview = requestAddress(url).includes("preview");
        if (!preview) {
          events.push("send");
          if (lost && first) {
            first = false;
            throw new TypeError("lost response");
          }
        }
        const operationExecuteRequest = command as RequestByEndpoint["operation.execute"];
        return new Response(
          JSON.stringify({
            representation: "HUMAN",
            operationId: operationExecuteRequest.operationId,
            outcome: preview
              ? {
                  kind: "REVIEWED",
                  review,
                }
              : {
                  kind: "ACCEPTED",
                  document: receipt,
                },
          }),
          {
            status: 200,
            headers: {
              "Content-Type": "application/json",
            },
          },
        );
      }),
    ),
  );
  let model: OperationModel | undefined;
  const onRecorded = vi.fn();
  const lock = vi.fn();
  const Harness = () => {
    const value = useOperation({
      subject: "CASE",
      kind: "case.close",
      current,
      credential: "csrf",
      contextEpoch: 1,
      recoveryScope: scope,
      journal,
      onLockChange: lock,
      onRecorded,
    });
    model = value;
    const preview = useRef<HTMLButtonElement>(null);
    if (panel) {
      return (
        <OperationPanel
          model={value}
          current={current}
          offered={["case.close"]}
          onClose={() => undefined}
        />
      );
    }
    return (
      <>
        <OperationForm
          model={value}
          current={current}
          offered={["case.close"]}
          onAct={() => undefined}
          formId="task-form"
        />
        <OperationFormActions
          model={value}
          current={current}
          offered={["case.close"]}
          formId="task-form"
          preview={preview}
        />
        <OperationReview model={value}>
          {(parts) =>
            parts === null ? null : (
              <>
                {parts.body}
                {parts.actions}
              </>
            )
          }
        </OperationReview>
      </>
    );
  };
  const view = render(<Harness />);
  return {
    view,
    events,
    bodies,
    journal,
    records,
    onRecorded,
    getModel: () => {
      if (model === undefined) {
        throw new Error("No model");
      }
      return model;
    },
  };
};
it("connects the actual form, Core review and journal-before-send to one exact prepared command", async () => {
  const f = fixture();
  expect(f.getModel().values).toEqual({});
  await userEvent.click(
    f.view.container.querySelector<HTMLButtonElement>('button[type="submit"]')!,
  );
  await waitFor(() => {
    expect(f.getModel().phase).toBe("REVIEWING");
  });
  await userEvent.click(
    f.view.container.querySelector<HTMLButtonElement>("[data-confirm-operation]")!,
  );
  await waitFor(() => {
    expect(f.getModel().phase).toBe("ACCEPTED");
  });
  expect(f.bodies).toHaveLength(2);
  expect(f.bodies[1]).toBe(f.bodies[0]);
  expect(JSON.parse(f.bodies[0]!)).toMatchObject({
    task: "case.close",
    binding: {
      kind: "CASE",
      target: {
        kind: "ID",
        caseId,
      },
      expectedRevision: 7,
    },
    inputs: {},
  });
  expect(f.events).toEqual(["journal", "send", "cleanup"]);
  expect(f.onRecorded).toHaveBeenCalledExactlyOnceWith(receipt);
});
it("retries an uncertain attempt with its original bytes and identity", async () => {
  const f = fixture(true);
  await act(() => f.getModel().preview());
  await waitFor(() => {
    expect(f.getModel().phase).toBe("REVIEWING");
  });
  await act(async () => {
    f.getModel().confirm(true);
    await f.getModel().execute();
  });
  await waitFor(() => {
    expect(f.getModel().phase).toBe("UNRESOLVED");
  });
  const original = f.getModel().prepared;
  await act(() => f.getModel().execute());
  await waitFor(() => {
    expect(f.getModel().phase).toBe("ACCEPTED");
  });
  expect(f.bodies).toHaveLength(3);
  expect(new Set(f.bodies).size).toBe(1);
  expect(f.getModel().prepared).toBe(original);
});
it("never dispatches execution when the original owner's journal write fails", async () => {
  const f = fixture(false, true);
  await act(() => f.getModel().preview());
  await waitFor(() => {
    expect(f.getModel().phase).toBe("REVIEWING");
  });
  await act(async () => {
    f.getModel().confirm(true);
    await f.getModel().execute();
  });
  await waitFor(() => {
    expect(f.getModel().phase).toBe("EDITING");
  });
  expect(f.bodies).toHaveLength(1);
  expect(f.events).toEqual(["journal"]);
  expect(f.getModel().notice).toMatchObject({
    reason: "recoveryUnavailable",
  });
});
it("connects the actual dialog with supplied target content, Core review and final consent", async () => {
  const f = fixture(false, false, true);
  const dialog = screen.getByRole("dialog");
  expect(within(dialog).getAllByText("Core opaque target Ω")).toHaveLength(2);
  await userEvent.click(
    within(dialog).getByRole("button", {
      name: "Review changes",
    }),
  );
  await waitFor(() => {
    expect(f.getModel().phase).toBe("REVIEWING");
  });
  await userEvent.click(dialog.querySelector<HTMLButtonElement>("[data-confirm-operation]")!);
  await waitFor(() => {
    expect(f.getModel().phase).toBe("ACCEPTED");
  });
  expect(f.bodies[1]).toBe(f.bodies[0]);
  expect(f.onRecorded).toHaveBeenCalledExactlyOnceWith(receipt);
});
