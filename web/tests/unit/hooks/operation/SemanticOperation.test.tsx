import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { act, waitFor } from "@testing-library/react";
import { useRef } from "react";
import { isRequest, type HumanReview, type HumanReceiptDocument } from "@contracts/client";
import { render } from "../../../support/presentation-test-support";
import { useOperation, type OperationModel } from "../../../../src/hooks/operation/useOperation";
import { OperationForm, OperationFormActions } from "../../../../src/views/operation/OperationForm";
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
  screen: { regions: [] },
  context: {
    kind: "CASE",
    caseId,
    revision: 7,
    workRevision: 2,
    history: "NONE",
    tasks: ["CASE:CLOSE"],
    editableInputs: [],
    correction: null,
    seeds: { registration: null, decision: null, work: null },
    notes: [],
  },
  basis,
  disclosure: "CURRENT",
};
const review: HumanReview = {
  presentation: { changes: [], unchanged: [] },
  document: {
    screen: { regions: [] },
    context: {
      kind: "REVIEW",
      task: "CASE:CLOSE",
      target: { kind: "CASE", caseId, beforeRevision: 7 },
      inputs: ["input:operationId", "field:caseReference", "input:expectedRevision"],
      consequence: "ROUTINE",
      confirmation: { kind: "CASE_REFERENCE", reference: "CORE-CASE" },
    },
    basis,
    disclosure: "CURRENT",
  },
};
const receipt: HumanReceiptDocument = {
  screen: { regions: [] },
  context: { kind: "RECEIPT", target: { kind: "CASE", caseId, revision: 8 }, refreshCase: caseId },
  basis,
  disclosure: "CURRENT",
};
afterEach(() => vi.unstubAllGlobals());
const fixture = (lost = false, putFails = false) => {
  const events: string[] = [];
  const bodies: string[] = [];
  const records: RecoveryRecord[] = [];
  const journal: RecoveryJournal = {
    list: vi.fn(async () => records),
    put: vi.fn(async (record) => {
      events.push("journal");
      if (putFails) {
        throw new Error("journal failure");
      }
      records.push(record);
    }),
    remove: vi.fn(async (record) => {
      events.push("cleanup");
      const index = records.indexOf(record);
      if (index >= 0) {
        records.splice(index, 1);
      }
    }),
  };
  let first = true;
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation(async (url, options) => {
      const body = String(options?.body);
      const command: unknown = JSON.parse(body);
      if (!isRequest("operation.execute", command)) {
        throw new Error("Expected an admitted typed command.");
      }
      bodies.push(body);
      const preview = String(url).includes("preview");
      if (!preview) {
        events.push("send");
        if (lost && first) {
          first = false;
          throw new TypeError("lost response");
        }
      }
      return new Response(
        JSON.stringify({
          representation: "HUMAN",
          operationId: command.operationId,
          outcome: preview ? { kind: "REVIEWED", review } : { kind: "ACCEPTED", document: receipt },
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      );
    }),
  );
  let model: OperationModel | undefined;
  const onRecorded = vi.fn();
  const lock = vi.fn();
  const Harness = () => {
    const value = useOperation({
      subject: "CASE",
      kind: "CLOSE",
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
    return (
      <>
        <OperationForm
          model={value}
          current={current}
          offered={["CLOSE"]}
          onAct={() => undefined}
          formId="task-form"
        />
        <OperationFormActions
          model={value}
          current={current}
          offered={["CLOSE"]}
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
  await waitFor(() => expect(f.getModel().phase).toBe("REVIEWING"));
  await userEvent.click(
    f.view.container.querySelector<HTMLButtonElement>("[data-confirm-operation]")!,
  );
  await waitFor(() => expect(f.getModel().phase).toBe("ACCEPTED"));
  expect(f.bodies).toHaveLength(2);
  expect(f.bodies[1]).toBe(f.bodies[0]);
  expect(JSON.parse(f.bodies[0]!)).toMatchObject({
    kind: "CASE",
    target: { kind: "ID", caseId },
    expectedRevision: 7,
    act: { kind: "CLOSE" },
  });
  expect(f.events).toEqual(["journal", "send", "cleanup"]);
  expect(f.onRecorded).toHaveBeenCalledExactlyOnceWith(receipt);
});

it("retries an uncertain attempt with its original bytes and identity", async () => {
  const f = fixture(true);
  await act(async () => f.getModel().preview());
  await waitFor(() => expect(f.getModel().phase).toBe("REVIEWING"));
  await act(async () => {
    f.getModel().confirm(true);
    await f.getModel().execute();
  });
  await waitFor(() => expect(f.getModel().phase).toBe("UNRESOLVED"));
  const original = f.getModel().prepared;
  await act(async () => f.getModel().execute());
  await waitFor(() => expect(f.getModel().phase).toBe("ACCEPTED"));
  expect(f.bodies).toHaveLength(3);
  expect(new Set(f.bodies).size).toBe(1);
  expect(f.getModel().prepared).toBe(original);
});

it("never dispatches execution when the original owner's journal write fails", async () => {
  const f = fixture(false, true);
  await act(async () => f.getModel().preview());
  await waitFor(() => expect(f.getModel().phase).toBe("REVIEWING"));
  await act(async () => {
    f.getModel().confirm(true);
    await f.getModel().execute();
  });
  await waitFor(() => expect(f.getModel().phase).toBe("EDITING"));
  expect(f.bodies).toHaveLength(1);
  expect(f.events).toEqual(["journal"]);
  expect(f.getModel().notice).toMatchObject({ reason: "recoveryUnavailable" });
});
