import { useEndpointCache } from "../../../../src/hooks/EndpointProvider";
import { afterEach, expect, it, vi } from "vitest";
import { act, waitFor } from "@testing-library/react";
import { render } from "../../../support/presentation-test-support";
import { activeRequester } from "../../../support/human-requester.fixtures";
import { RequesterContext } from "../../../../src/hooks/useRequesterContext";
import { useOperation, type OperationModel } from "../../../../src/hooks/operation/useOperation";
import type { RecoveryJournal, RecoveryRecord } from "../../../../src/protocol/recoveryJournal";
import type { CurrentCase } from "../../../../src/protocol/types";
import { isRequest, type HumanReview, type HumanReceiptDocument } from "@contracts/client";

const caseId = "00000000-0000-4000-8000-000000000001";
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
    workRevision: 0,
    history: "NONE",
    tasks: ["CASE:CLOSE"],
    editableInputs: [],
    correction: null,
    seeds: { registration: null, decision: null, work: null, paymentCurrency: null },
    notes: [],
    payments: [],
    nextActions: [],
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
  screen: {
    regions: [
      {
        role: "FACTS",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "private",
              labelId: "label.details",
              prominence: "PRIMARY",
              value: { kind: "TEXT", value: "Private original acceptance" },
            },
          ],
        },
      },
    ],
  },
  context: { kind: "RECEIPT", target: { kind: "CASE", caseId, revision: 8 }, refreshCase: caseId },
  basis,
  disclosure: "CURRENT",
};
afterEach(() => vi.unstubAllGlobals());
const fixture = async (lost = false, holdJournal = false, cleanupFails = false) => {
  const records: RecoveryRecord[] = [];
  const bodies: string[] = [];
  let finishJournal: () => void = () => {
    throw new Error("No journal write pending");
  };
  const journal: RecoveryJournal = {
    list: vi.fn(async () => [...records]),
    put: vi.fn(async (record) => {
      if (holdJournal) {
        await new Promise<void>((resolve) => {
          finishJournal = resolve;
        });
      }
      if (!records.includes(record)) {
        records.push(record);
      }
    }),
    remove: vi.fn(async (record) => {
      if (cleanupFails) {
        throw new Error("Original owner cleanup failure");
      }
      records.splice(records.indexOf(record), 1);
    }),
  };
  let complete: () => void = () => {
    throw new Error("No delivery pending");
  };
  let executions = 0;
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockImplementation(async (url, options) => {
      const input: unknown = JSON.parse(String(options?.body));
      if (!isRequest("operation.execute", input)) {
        throw new Error("Unexpected request");
      }
      bodies.push(String(options?.body));
      const response = (outcome: unknown) =>
        new Response(
          JSON.stringify({ representation: "HUMAN", operationId: input.operationId, outcome }),
          { status: 200, headers: { "Content-Type": "application/json" } },
        );
      if (!String(url).endsWith("preview")) {
        executions += 1;
        if (lost && executions === 1) {
          throw new TypeError("Lost first response");
        }
      }
      return String(url).endsWith("preview")
        ? response({ kind: "REVIEWED", review })
        : new Promise<Response>((resolve) => {
            complete = () => resolve(response({ kind: "ACCEPTED", document: receipt }));
          });
    }),
  );
  let model: OperationModel | undefined;
  let invalidate = () => {
    throw new Error("No cache");
  };
  const recorded = vi.fn();
  const Harness = ({ credential, epoch }: { credential: string; epoch: number }) => {
    const cache = useEndpointCache();
    invalidate = () => {
      cache?.invalidate();
    };
    model = useOperation({
      subject: "CASE",
      kind: "CLOSE",
      current,
      credential,
      contextEpoch: epoch,
      journal,
      onLockChange: vi.fn(),
      onRecorded: recorded,
    });
    return null;
  };
  const ui = (available: boolean, credential = "token", epoch = 0) => (
    <RequesterContext value={available ? activeRequester : null}>
      <Harness credential={credential} epoch={epoch} />
    </RequesterContext>
  );
  const view = render(ui(true));
  const get = () => {
    if (model === undefined) {
      throw new Error("No model");
    }
    return model;
  };
  await act(async () => get().preview());
  await waitFor(() => expect(get().phase).toBe("REVIEWING"));
  await act(async () => {
    get().confirm(true);
  });
  await act(async () => get().execute());
  await waitFor(() =>
    expect(get().phase).toBe(holdJournal ? "PREPARING" : lost ? "UNRESOLVED" : "SUBMITTING"),
  );
  return {
    get,
    view,
    ui,
    complete: () => complete(),
    completeJournal: () => finishJournal(),
    invalidate: () => invalidate(),
    records,
    bodies,
    journal,
    recorded,
  };
};
it("settles acceptance under the original journal owner after requester/display invalidation without publishing private output", async () => {
  const f = await fixture();
  const original = f.get().prepared;
  f.view.rerender(f.ui(false));
  await act(async () => f.get().invalidateDisclosure());
  await act(async () => f.complete());
  await waitFor(() => expect(f.records).toHaveLength(0));
  expect(f.get().phase).toBe("ACCEPTED");
  expect(f.get().receipt).toBeNull();
  expect(f.get().prepared).toBe(original);
  expect(f.recorded).not.toHaveBeenCalled();
  expect(f.bodies).toHaveLength(2);
  expect(f.bodies[1]).toBe(f.bodies[0]);
});
it("settles a confirmed dispatched acceptance after unmount without notifying a replacement display", async () => {
  const f = await fixture();
  f.view.unmount();
  await act(async () => f.complete());
  await waitFor(() => expect(f.records).toHaveLength(0));
  expect(f.journal.remove).toHaveBeenCalledTimes(1);
  expect(f.recorded).not.toHaveBeenCalled();
  expect(f.bodies).toHaveLength(2);
});

it("settles an exact retry after display invalidation without another send or identity", async () => {
  const f = await fixture(true);
  const original = f.get().prepared;
  await act(async () => f.get().execute());
  await waitFor(() => expect(f.get().busy).toBe(true));
  f.view.rerender(f.ui(false));
  await act(async () => f.get().invalidateDisclosure());
  await act(async () => f.complete());
  await waitFor(() => expect(f.records).toHaveLength(0));
  expect(f.get().phase).toBe("ACCEPTED");
  expect(f.get().receipt).toBeNull();
  expect(f.get().prepared).toBe(original);
  expect(f.bodies).toHaveLength(3);
  expect(new Set(f.bodies).size).toBe(1);
  expect(f.recorded).not.toHaveBeenCalled();
});

it("a replacement session cannot send the retained private request from an earlier session", async () => {
  const f = await fixture(true);
  f.view.rerender(f.ui(true, "replacement-credential", 1));
  expect(f.get().ownerVerified).toBe(false);
  await act(async () => f.get().execute());
  expect(f.bodies).toHaveLength(2);
  expect(f.records).toHaveLength(1);
});

it("does not send if disclosure changes while the original journal write is pending", async () => {
  const f = await fixture(false, true);
  await waitFor(() => expect(f.journal.put).toHaveBeenCalledTimes(1));
  f.view.rerender(f.ui(false));
  await act(async () => f.get().invalidateDisclosure());
  await act(async () => f.completeJournal());
  expect(f.bodies).toHaveLength(1);
  expect(f.get().phase).toBe("EDITING");
  expect(f.records).toHaveLength(1);
  expect(f.recorded).not.toHaveBeenCalled();
});
it("keeps confirmed acceptance and its recovery identity when original-owner cleanup fails", async () => {
  const f = await fixture(false, false, true);
  f.view.rerender(f.ui(false));
  await act(async () => f.get().invalidateDisclosure());
  await act(async () => f.complete());
  await waitFor(() => expect(f.get().phase).toBe("ACCEPTED"));
  expect(f.records).toHaveLength(1);
  expect(f.get().receipt).toBeNull();
  expect(f.bodies).toHaveLength(2);
  expect(f.recorded).not.toHaveBeenCalled();
});

it("a cache generation change masks the accepted artifact even when requester props stay unchanged", async () => {
  const f = await fixture();
  await act(async () => f.complete());
  await waitFor(() => expect(f.get().phase).toBe("ACCEPTED"));
  expect(f.get().receipt).not.toBeNull();
  await act(async () => f.invalidate());
  expect(f.get().receipt).toBeNull();
  expect(f.get().currentCase).toBeNull();
  expect(f.get().phase).toBe("ACCEPTED");
  expect(f.bodies).toHaveLength(2);
});
it("a cache generation change during delivery settles without republishing the accepted artifact", async () => {
  const f = await fixture();
  await act(async () => f.invalidate());
  expect(f.get().busy).toBe(true);
  await act(async () => f.complete());
  await waitFor(() => expect(f.get().phase).toBe("ACCEPTED"));
  expect(f.records).toHaveLength(0);
  expect(f.get().receipt).toBeNull();
  expect(f.recorded).not.toHaveBeenCalled();
  expect(f.bodies).toHaveLength(2);
});

it("a cache invalidation cancels handoff while the original journal write is pending", async () => {
  const f = await fixture(false, true);
  await waitFor(() => expect(f.journal.put).toHaveBeenCalledTimes(1));
  await act(async () => f.invalidate());
  await act(async () => f.completeJournal());
  expect(f.bodies).toHaveLength(1);
  expect(f.get().phase).toBe("EDITING");
  expect(f.recorded).not.toHaveBeenCalled();
});
