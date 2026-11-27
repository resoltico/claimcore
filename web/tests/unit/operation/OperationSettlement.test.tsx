import { operationAffordance } from "../../support/affordances.fixtures";
import type {
  RequestByEndpoint,
  HumanReview,
  HumanDocument,
  HumanLookupResult,
} from "@contracts/client";
import { requestText, requestAddress } from "../../support/transport-response.fixtures";
import { useEndpointCache } from "../../../src/hooks/EndpointProvider";
import { service } from "../../../src/api/service";
import { afterEach, expect, it, vi } from "vitest";
import { act, waitFor } from "@testing-library/react";
import { render } from "../../support/presentation-test-support";
import { activeRequester } from "../../support/human-requester.fixtures";
import { absentLookup, pendingLookup } from "../../support/operation-lookup.fixtures";
import { RequesterContext } from "../../../src/hooks/useRequesterContext";
import { useOperation, type OperationModel } from "../../../src/operation/useOperation";
import { runOperationEffect } from "../../../src/operation/operationEffects";
import type { RecoveryJournal, RecoveryRecord } from "../../../src/protocol/recoveryJournal";
import { prepareRequest } from "../../../src/protocol/preparedRequest";
import type { CurrentCase } from "../../../src/protocol/types";
const caseId = "00000000-0000-4000-8000-000000000001";
it.each([false, true])(
  "retains a terminal attempt in memory only when its durable retirement fails (%s)",
  async (cleanupFails) => {
    const scope = {
      serviceOrigin: "https://claimcore.example",
      installationId: "00000000-0000-4000-8000-000000000002",
      actorId: "00000000-0000-4000-8000-000000000003",
    };
    const operationId = "10000000-0000-4000-8000-000000000001";
    const attempt: RecoveryRecord = {
      ...scope,
      format: 3,
      attemptId: "00000000-0000-4000-8000-000000000004",
      operationId,
      subject: "CASE",
      act: "case.close",
    };
    const attempts = new Map([[operationId, attempt]]);
    const remove: RecoveryJournal["remove"] = vi.fn(() =>
      cleanupFails ? Promise.reject(new Error("Storage denied")) : Promise.resolve(),
    );
    vi.spyOn(service, "operationGet").mockResolvedValue(absentLookup(operationId));
    const decision = await runOperationEffect(
      {
        kind: "CHECK_OPERATION",
        identity: {
          owner: { ...scope, sessionEpoch: 0 },
          id: "lookup",
          credential: "test",
          contextGeneration: 0,
          target: "case",
        },
        source: {
          prepared: prepareRequest("operation.execute", {
            operationId,
            task: "case.close",
            binding: { kind: "CASE", target: { kind: "ID", caseId }, expectedRevision: 1 },
            inputs: { "field:outcome": "OTHER" },
          }),
          submittedRows: {},
        },
      },
      {
        journal: {
          list: () => Promise.resolve([attempt]),
          put: () => Promise.resolve("existing"),
          remove,
        },
        current: () => true,
        handoff: vi.fn(),
        upload: undefined,
        scope,
        attempts,
        reads: new Map(),
        transfers: new Map(),
      },
    );
    expect(decision.type).toBe("NOT_ACCEPTED");
    expect(remove).toHaveBeenCalledExactlyOnceWith(attempt);
    expect(attempts.get(operationId)).toBe(cleanupFails ? attempt : undefined);
  },
);
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
    regions: [],
  },
  subject: { kind: "CASE", caseId, revision: 7, workRevision: 0 },
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
const receipt: HumanDocument = {
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
              value: {
                kind: "TEXT",
                value: "Private original acceptance",
              },
            },
          ],
        },
      },
    ],
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
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});
type ExecuteReply = (operationId: string) => Response;
const humanReply = (operationId: string, outcome: unknown) =>
  Response.json({ representation: "HUMAN", operationId, outcome });
const notStarted: ExecuteReply = () =>
  Response.json({ hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } }, { status: 429 });
const fixture = async (
  lost = false,
  holdJournal = false,
  cleanupFails = false,
  status?: HumanLookupResult,
) => {
  const records: RecoveryRecord[] = [];
  const bodies: string[] = [];
  const lookups: string[] = [];
  let finishJournal: () => void = () => {
    throw new Error("No journal write pending");
  };
  const journal: RecoveryJournal = {
    list: vi.fn(() => Promise.try(() => [...records])),
    put: vi.fn(async (record: RecoveryRecord) => {
      if (holdJournal) {
        await new Promise<void>((resolve) => {
          finishJournal = resolve;
        });
      }
      if (records.includes(record)) {
        return "existing";
      }
      records.push(record);
      return "inserted";
    }),
    remove: vi.fn((record: RecoveryRecord) =>
      Promise.try(() => {
        if (cleanupFails) {
          throw new Error("Original owner cleanup failure");
        }
        records.splice(records.indexOf(record), 1);
      }),
    ),
  };
  let complete: (reply?: ExecuteReply) => void = () => {
    throw new Error("No delivery pending");
  };
  let executions = 0;
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockImplementation((url, options) =>
      Promise.try(() => {
        const input: unknown = JSON.parse(requestText(options?.body));
        if (requestAddress(url).endsWith("operation.get") && status !== undefined) {
          const operationGetRequest = input as RequestByEndpoint["operation.get"];
          lookups.push(operationGetRequest.operationId);
          return new Response(
            JSON.stringify({
              representation: "HUMAN",
              operationId: operationGetRequest.operationId,
              outcome: status,
            }),
            {
              status: 200,
              headers: {
                "Content-Type": "application/json",
              },
            },
          );
        }
        bodies.push(requestText(options?.body));
        const operationExecuteRequest = input as RequestByEndpoint["operation.execute"];
        const response = (outcome: unknown) =>
          humanReply(operationExecuteRequest.operationId, outcome);
        if (!requestAddress(url).endsWith("preview")) {
          executions += 1;
          if (lost && executions === 1) {
            throw new TypeError("Lost first response");
          }
        }
        return requestAddress(url).endsWith("preview")
          ? response({
              kind: "REVIEWED",
              review,
            })
          : new Promise<Response>((resolve) => {
              complete = (reply) => {
                resolve(
                  reply?.(operationExecuteRequest.operationId) ??
                    response({
                      kind: "ACCEPTED",
                      document: receipt,
                    }),
                );
              };
            });
      }),
    ),
  );
  let model: OperationModel | undefined;
  let invalidate: () => void = () => {
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
      kind: "case.close",
      current,
      credential,
      contextEpoch: epoch,
      journal,
      onLockChange: vi.fn(),
      onRecorded: recorded,
    });
    return null;
  };
  const ui = (available: boolean, credential = "token", epoch = 0, requester = activeRequester) => (
    <RequesterContext value={available ? requester : null}>
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
  await act(() => get().preview());
  await waitFor(() => {
    expect(get().phase).toBe("REVIEWING");
  });
  await act(() =>
    Promise.try(() => {
      get().confirm(true);
    }),
  );
  await act(() => get().execute());
  let expectedPhase = "SUBMITTING";
  if (lost) {
    expectedPhase = "UNRESOLVED";
  }
  if (holdJournal) {
    expectedPhase = "PREPARING";
  }
  await waitFor(() => {
    expect(get().phase).toBe(expectedPhase);
  });
  return {
    get,
    view,
    ui,
    complete: (reply?: ExecuteReply) => {
      complete(reply);
    },
    completeJournal: () => {
      finishJournal();
    },
    invalidate: () => {
      invalidate();
    },
    records,
    bodies,
    lookups,
    journal,
    recorded,
  };
};
it.each<{ description: string; reply: ExecuteReply; notice: string }>([
  { description: "explicit non-start", reply: notStarted, notice: "diagnostic" },
  {
    description: "a typed refusal",
    notice: "human",
    reply: (id) =>
      humanReply(id, {
        kind: "REJECTED",
        rejection: {
          kind: "REFUSED",
          advice: {
            message: { messageId: "diagnostic.NO_CHANGES", arguments: [] },
            action: "NONE_REQUIRED",
          },
        },
      }),
  },
  {
    description: "a null delivery phase",
    notice: "diagnostic",
    reply: () =>
      Response.json({ hostFailure: { code: "RESPONSE_FAILED", phase: null } }, { status: 500 }),
  },
  {
    description: "an unknown delivery phase",
    notice: "invalidHttp",
    reply: () =>
      Response.json({ hostFailure: { code: "RATE_LIMITED", phase: "UNKNOWN" } }, { status: 429 }),
  },
  {
    description: "a success correlated to another operation",
    notice: "invalidHttp",
    reply: () =>
      humanReply("10000000-0000-4000-8000-000000000099", { kind: "ACCEPTED", document: receipt }),
  },
  {
    description: "a structurally malformed success",
    notice: "invalidHttp",
    reply: (id) => humanReply(id, { kind: "ACCEPTED", document: {} }),
  },
])(
  "retains the original uncertain attempt after retry gives $description",
  async ({ reply, notice }) => {
    const lookup = vi.spyOn(service, "operationGet");
    const f = await fixture(true, false, false, pendingLookup(caseId).value.outcome);
    const [original] = f.records;
    expect(original).toBeDefined();
    await act(() => f.get().execute());
    await waitFor(() => {
      expect(f.get().busy).toBe(true);
    });
    await act(() =>
      Promise.try(() => {
        f.complete(reply);
      }),
    );
    await waitFor(() => {
      expect(f.get().busy).toBe(false);
    });
    expect(f.get().phase).toBe("UNRESOLVED");
    expect(f.get().notice?.kind).toBe(notice);
    expect(f.records).toEqual([original]);
    expect(f.journal.remove).not.toHaveBeenCalled();
    expect(f.bodies).toHaveLength(3);
    expect(new Set(f.bodies).size).toBe(1);
    await act(() =>
      Promise.try(() => {
        f.get().checkOperation();
      }),
    );
    await waitFor(() => {
      expect(f.lookups).toEqual([original?.operationId]);
      expect(f.get().busy).toBe(false);
    });
    const [observed] = lookup.mock.results;
    await expect(observed?.value).resolves.toMatchObject({
      kind: "outcome",
      value: { operationId: original?.operationId, outcome: { kind: "PENDING" } },
    });
    expect(f.get().phase).toBe("UNRESOLVED");
    expect(f.records).toEqual([original]);
    expect(f.journal.remove).not.toHaveBeenCalled();
  },
);
it("settles acceptance under the original journal owner after requester/display invalidation without publishing private output", async () => {
  const f = await fixture();
  const original = f.get().prepared;
  f.view.rerender(f.ui(false));
  await act(() =>
    Promise.try(() => {
      f.get().invalidateDisclosure();
    }),
  );
  await act(() =>
    Promise.try(() => {
      f.complete();
    }),
  );
  await waitFor(() => {
    expect(f.records).toHaveLength(0);
  });
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
  await act(() =>
    Promise.try(() => {
      f.complete();
    }),
  );
  await waitFor(() => {
    expect(f.records).toHaveLength(0);
  });
  expect(f.journal.remove).toHaveBeenCalledTimes(1);
  expect(f.recorded).not.toHaveBeenCalled();
  expect(f.bodies).toHaveLength(2);
});
it("settles an exact retry after display invalidation without another send or identity", async () => {
  const f = await fixture(true);
  const original = f.get().prepared;
  await act(() => f.get().execute());
  await waitFor(() => {
    expect(f.get().busy).toBe(true);
  });
  f.view.rerender(f.ui(false));
  await act(() =>
    Promise.try(() => {
      f.get().invalidateDisclosure();
    }),
  );
  await act(() =>
    Promise.try(() => {
      f.complete();
    }),
  );
  await waitFor(() => {
    expect(f.records).toHaveLength(0);
  });
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
  await act(() => f.get().execute());
  expect(f.bodies).toHaveLength(2);
  expect(f.records).toHaveLength(1);
});
it("does not send if disclosure changes while the original journal write is pending", async () => {
  const f = await fixture(false, true);
  await waitFor(() => {
    expect(f.journal.put).toHaveBeenCalledTimes(1);
  });
  f.view.rerender(f.ui(false));
  await act(() =>
    Promise.try(() => {
      f.get().invalidateDisclosure();
    }),
  );
  await act(() =>
    Promise.try(() => {
      f.completeJournal();
    }),
  );
  expect(f.bodies).toHaveLength(1);
  expect(f.get().phase).toBe("EDITING");
  expect(f.records).toHaveLength(1);
  expect(f.recorded).not.toHaveBeenCalled();
});
it.each(["actorId", "installationId", "epoch"] as const)(
  "a changed %s prevents handoff after a durable save and retains the original record",
  async (change) => {
    const f = await fixture(false, true);
    await waitFor(() => {
      expect(f.journal.put).toHaveBeenCalledTimes(1);
    });
    const original = vi.mocked(f.journal.put).mock.calls[0]?.[0];
    const requester =
      change === "epoch"
        ? activeRequester
        : {
            ...activeRequester,
            subject: {
              ...activeRequester.subject,
              [change]: "10000000-0000-4000-8000-000000000099",
            },
          };
    f.view.rerender(f.ui(true, "token", change === "epoch" ? 1 : 0, requester));
    await act(() =>
      Promise.try(() => {
        f.completeJournal();
      }),
    );
    expect(f.get().ownerVerified).toBe(false);
    expect(f.get().receipt).toBeNull();
    expect(f.get().notice).toBeNull();
    expect(f.records).toEqual([original]);
    expect(f.journal.remove).not.toHaveBeenCalled();
    expect(f.bodies).toHaveLength(1);
  },
);
it("a late original refusal settles only the original record after an owner change", async () => {
  const f = await fixture();
  const [original] = f.records;
  if (original === undefined) {
    throw new Error("Expected the original owner's durable record");
  }
  const nextOwner = {
    ...activeRequester,
    subject: {
      ...activeRequester.subject,
      actorId: "10000000-0000-4000-8000-000000000099",
      installationId: "90000000-0000-4000-8000-000000000099",
    },
  };
  const otherRecord = {
    ...original,
    actorId: nextOwner.subject.actorId,
    installationId: nextOwner.subject.installationId,
  };
  f.records.push(otherRecord);
  f.view.rerender(f.ui(true, "token", 1, nextOwner));
  await act(() =>
    Promise.try(() => {
      f.complete(notStarted);
    }),
  );
  await waitFor(() => {
    expect(f.journal.remove).toHaveBeenCalledExactlyOnceWith(original);
  });
  expect(f.records).toEqual([otherRecord]);
  expect(f.get().notice).toBeNull();
  expect(f.get().receipt).toBeNull();
  expect(f.recorded).not.toHaveBeenCalled();
});
it("retains the record and original diagnostic when known-refusal cleanup fails", async () => {
  const f = await fixture(false, false, true);
  const [original] = f.records;
  await act(() =>
    Promise.try(() => {
      f.complete(notStarted);
    }),
  );
  await waitFor(() => {
    expect(f.get().notice).toEqual({
      kind: "diagnostic",
      diagnostic: { code: "RATE_LIMITED", phase: "NOT_STARTED" },
    });
  });
  expect(f.journal.remove).toHaveBeenCalledExactlyOnceWith(original);
  expect(f.records).toEqual([original]);
  expect(f.get().phase).toBe("EDITING");
});
it("keeps confirmed acceptance and its recovery identity when original-owner cleanup fails", async () => {
  const f = await fixture(false, false, true);
  f.view.rerender(f.ui(false));
  await act(() =>
    Promise.try(() => {
      f.get().invalidateDisclosure();
    }),
  );
  await act(() =>
    Promise.try(() => {
      f.complete();
    }),
  );
  await waitFor(() => {
    expect(f.get().phase).toBe("ACCEPTED");
  });
  expect(f.records).toHaveLength(1);
  expect(f.get().receipt).toBeNull();
  expect(f.bodies).toHaveLength(2);
  expect(f.recorded).not.toHaveBeenCalled();
});
it("a cache generation change masks the accepted artifact even when requester props stay unchanged", async () => {
  const f = await fixture();
  await act(() =>
    Promise.try(() => {
      f.complete();
    }),
  );
  await waitFor(() => {
    expect(f.get().phase).toBe("ACCEPTED");
  });
  expect(f.get().receipt).not.toBeNull();
  await act(() =>
    Promise.try(() => {
      f.invalidate();
    }),
  );
  expect(f.get().receipt).toBeNull();
  expect(f.get().currentCase).toBeNull();
  expect(f.get().phase).toBe("ACCEPTED");
  expect(f.bodies).toHaveLength(2);
});
it("a cache generation change during delivery settles without republishing the accepted artifact", async () => {
  const f = await fixture();
  await act(() =>
    Promise.try(() => {
      f.invalidate();
    }),
  );
  expect(f.get().busy).toBe(true);
  await act(() =>
    Promise.try(() => {
      f.complete();
    }),
  );
  await waitFor(() => {
    expect(f.get().phase).toBe("ACCEPTED");
  });
  expect(f.records).toHaveLength(0);
  expect(f.get().receipt).toBeNull();
  expect(f.recorded).not.toHaveBeenCalled();
  expect(f.bodies).toHaveLength(2);
});
it("a cache invalidation cancels handoff while the original journal write is pending", async () => {
  const f = await fixture(false, true);
  await waitFor(() => {
    expect(f.journal.put).toHaveBeenCalledTimes(1);
  });
  await act(() =>
    Promise.try(() => {
      f.invalidate();
    }),
  );
  await act(() =>
    Promise.try(() => {
      f.completeJournal();
    }),
  );
  expect(f.bodies).toHaveLength(1);
  expect(f.get().phase).toBe("EDITING");
  expect(f.recorded).not.toHaveBeenCalled();
});
const absence: HumanLookupResult = {
  kind: "NOT_ACCEPTED",
  document: {
    screen: {
      regions: [],
    },
    subject: { kind: "LIST" },
    actions: [],
    links: [],
    history: null,
    nextCursor: null,
    basis: {
      kind: "UNAVAILABLE",
    },
    disclosure: "NONE",
  },
};
it.each([false, true])(
  "keeps definitive acceptance when lookup journal cleanup fails: %s",
  async (cleanupFails) => {
    const f = await fixture(true, false, cleanupFails, {
      kind: "ACCEPTED",
      byRequester: true,
      document: receipt,
    });
    const original = f.get().prepared;
    await act(() =>
      Promise.try(() => {
        f.get().checkOperation();
      }),
    );
    await waitFor(() => {
      expect(f.get().phase).toBe("ACCEPTED");
    });
    expect(f.get().prepared).toBe(original);
    expect(f.get().receipt).toEqual(receipt);
    expect(f.records).toHaveLength(cleanupFails ? 1 : 0);
    expect(f.bodies).toHaveLength(2);
  },
);
it.each([false, true])(
  "keeps final absence when lookup journal cleanup fails: %s",
  async (cleanupFails) => {
    const f = await fixture(true, false, cleanupFails, absence);
    await act(() =>
      Promise.try(() => {
        f.get().checkOperation();
      }),
    );
    await waitFor(() => {
      expect(f.get().phase).toBe("EDITING");
    });
    expect(f.get().priorUncertain).toBe(false);
    expect(f.get().notice).toEqual({
      kind: "local",
      reason: "notAccepted",
    });
    expect(f.get().receipt).toBeNull();
    expect(f.records).toHaveLength(cleanupFails ? 1 : 0);
    expect(f.bodies).toHaveLength(2);
  },
);
it("does not attribute a colliding operation accepted by another actor to the original requester", async () => {
  const f = await fixture(true, false, false, {
    kind: "ACCEPTED",
    byRequester: false,
    document: receipt,
  });
  await act(() =>
    Promise.try(() => {
      f.get().checkOperation();
    }),
  );
  await waitFor(() => {
    expect(f.get().phase).toBe("EDITING");
  });
  expect(f.get().receipt).toBeNull();
  expect(f.recorded).not.toHaveBeenCalled();
  expect(f.records).toHaveLength(0);
  expect(f.bodies).toHaveLength(2);
});
it.each(["WITHHELD", "ERASED"] as const)(
  "settles acceptance with a %s receipt without reconstructing private facts",
  async (disclosure) => {
    const masked: HumanDocument = {
      ...receipt,
      screen: {
        regions: [],
      },
      disclosure,
    };
    const f = await fixture(true, false, false, {
      kind: "ACCEPTED",
      byRequester: true,
      document: masked,
    });
    await act(() =>
      Promise.try(() => {
        f.get().checkOperation();
      }),
    );
    await waitFor(() => {
      expect(f.get().phase).toBe("ACCEPTED");
    });
    expect(f.get().receipt).toEqual(masked);
    expect(f.records).toHaveLength(0);
    expect(f.bodies).toHaveLength(2);
  },
);
