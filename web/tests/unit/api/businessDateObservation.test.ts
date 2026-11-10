import { expect, it, vi } from "vitest";
import {
  captureBusinessDateObserver,
  subscribeBusinessDates,
} from "../../../src/api/businessDateObservation";
const observation = (businessDate: string) => ({ kind: "SNAPSHOT", observation: { businessDate } });
const response = (date: string) => ({
  representation: "HUMAN",
  outcome: { kind: "SUCCEEDED", document: { basis: observation(date) } },
});
it("publishes only date-bearing successful observations dispatched in the current scope", () => {
  const observed = vi.fn();
  const close = subscribeBusinessDates({
    credential: "current",
    installationId: "installation",
    actorId: "actor",
    observed,
  });
  captureBusinessDateObserver("case.list", "other")(response("2026-11-05"));
  captureBusinessDateObserver(
    "case.list",
    "current",
  )({
    representation: "HUMAN",
    outcome: { kind: "REJECTED", document: { basis: observation("2026-11-05") } },
  });
  captureBusinessDateObserver(
    "case.list",
    "current",
  )({
    representation: "HUMAN",
    outcome: {
      kind: "SUCCEEDED",
      document: { screen: { regions: [] } },
    },
  });
  expect(observed).not.toHaveBeenCalled();
  captureBusinessDateObserver("case.list", "current")(response("2026-11-05"));
  expect(observed).toHaveBeenCalledExactlyOnceWith("2026-11-05");
  close();
});
it("observes the case workspace's owned work-date basis", () => {
  const observed = vi.fn();
  const close = subscribeBusinessDates({
    credential: "current",
    installationId: "installation",
    actorId: "actor",
    observed,
  });
  captureBusinessDateObserver(
    "case.get",
    "current",
  )({
    representation: "HUMAN",
    outcome: {
      kind: "SUCCEEDED",
      document: { basis: observation("2026-11-05") },
    },
  });
  expect(observed).toHaveBeenCalledExactlyOnceWith("2026-11-05");
  close();
});
it("rejects prior-session replies even when a new session reuses the credential and identity", () => {
  const first = vi.fn();
  const second = vi.fn();
  const closeFirst = subscribeBusinessDates({
    credential: "same",
    installationId: "installation",
    actorId: "actor",
    observed: first,
  });
  const late = captureBusinessDateObserver("case.list", "same");
  const closeSecond = subscribeBusinessDates({
    credential: "same",
    installationId: "installation",
    actorId: "actor",
    observed: second,
  });
  closeFirst();
  late(response("2026-11-05"));
  expect(first).not.toHaveBeenCalled();
  expect(second).not.toHaveBeenCalled();
  captureBusinessDateObserver("case.list", "same")(response("2026-11-06"));
  expect(second).toHaveBeenCalledExactlyOnceWith("2026-11-06");
  closeSecond();
});
it("ignores a stale dispatch and a context response for a different installation or actor", () => {
  const observed = vi.fn();
  const close = subscribeBusinessDates({
    credential: "current",
    installationId: "installation",
    actorId: "actor",
    observed,
  });
  const older = captureBusinessDateObserver("case.list", "current");
  captureBusinessDateObserver("case.list", "current")(response("2026-11-05"));
  older(response("2026-11-04"));
  for (const data of [
    { installationId: "other", actorId: "actor" },
    { installationId: "installation", actorId: "other" },
  ]) {
    captureBusinessDateObserver(
      "context",
      "current",
    )({
      representation: "HUMAN",
      outcome: {
        kind: "SUCCEEDED",
        document: { context: { kind: "REQUESTER", ...data }, basis: observation("2026-11-06") },
      },
    });
  }
  expect(observed).toHaveBeenCalledExactlyOnceWith("2026-11-05");
  captureBusinessDateObserver(
    "context",
    "current",
  )({
    representation: "HUMAN",
    outcome: {
      kind: "SUCCEEDED",
      document: {
        context: { kind: "REQUESTER", installationId: "installation", actorId: "actor" },
        basis: observation("2026-11-06"),
      },
    },
  });
  expect(observed).toHaveBeenLastCalledWith("2026-11-06");
  const late = captureBusinessDateObserver("case.list", "current");
  close();
  late(response("2026-11-07"));
  expect(observed).toHaveBeenCalledTimes(2);
});

it("does not publish an aborted validated read when its transport eventually finishes", async () => {
  const { service } = await import("../../../src/api/service");
  const transportResponse = () =>
    new Response(
      JSON.stringify({
        representation: "HUMAN",
        outcome: {
          kind: "SUCCEEDED",
          document: {
            screen: { regions: [] },
            context: { kind: "CASES", cases: [], nextCursor: null, exactCaseId: null, tasks: [] },
            basis: {
              kind: "SNAPSHOT",
              observation: {
                observedAt: "2026-11-05T12:00:00.000000Z",
                businessDate: "2026-11-05",
                timeZone: "Etc/UTC",
              },
            },
            disclosure: "CURRENT",
          },
        },
      }),
      { status: 200, headers: { "Content-Type": "application/json" } },
    );
  const observed = vi.fn();
  const close = subscribeBusinessDates({
    credential: "current",
    installationId: "installation",
    actorId: "actor",
    observed,
  });
  let finish: (response: Response) => void = () => undefined;
  const transport = new Promise<Response>((resolve) => {
    finish = resolve;
  });
  const fetch = vi.fn().mockResolvedValueOnce(transportResponse()).mockReturnValue(transport);
  vi.stubGlobal("fetch", fetch);
  const controller = new AbortController();
  try {
    expect((await service.list(null, 10, "current")).kind).toBe("outcome");
    expect(observed).toHaveBeenCalledExactlyOnceWith("2026-11-05");
    observed.mockClear();
    const read = service.list(null, 10, "current", controller.signal);
    await vi.waitFor(() => {
      expect(fetch).toHaveBeenCalledTimes(2);
    });
    controller.abort();
    finish(transportResponse());
    expect((await read).kind).toBe("deliveryFailure");
    expect(observed).not.toHaveBeenCalled();
  } finally {
    close();
    vi.unstubAllGlobals();
  }
});

it("observes core document dates from reviewed and accepted operations under the captured owner", () => {
  const observed = vi.fn();
  const close = subscribeBusinessDates({
    credential: "current",
    installationId: "installation",
    actorId: "actor",
    observed,
  });
  const document = { basis: { kind: "SNAPSHOT", observation: { businessDate: "2026-11-10" } } };
  captureBusinessDateObserver(
    "operation.preview",
    "current",
  )({ representation: "HUMAN", outcome: { kind: "REVIEWED", review: { document } } });
  expect(observed).toHaveBeenLastCalledWith("2026-11-10");
  const later = { basis: { kind: "SNAPSHOT", observation: { businessDate: "2026-11-11" } } };
  captureBusinessDateObserver(
    "operation.execute",
    "current",
  )({ representation: "HUMAN", outcome: { kind: "ACCEPTED", document: later } });
  expect(observed).toHaveBeenLastCalledWith("2026-11-11");
  close();
  captureBusinessDateObserver(
    "operation.execute",
    "current",
  )({ representation: "HUMAN", outcome: { kind: "ALREADY_ACCEPTED", document } });
  expect(observed).toHaveBeenCalledTimes(2);
});
