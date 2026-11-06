import { expect, it, vi } from "vitest";
import {
  captureBusinessDateObserver,
  subscribeBusinessDates,
} from "../../../src/api/businessDateObservation";
const response = (date: string) => ({
  outcome: { kind: "SUCCEEDED", data: { basis: { businessDate: date } } },
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
  )({ outcome: { kind: "REJECTED", data: { basis: { businessDate: "2026-11-05" } } } });
  captureBusinessDateObserver(
    "case.list",
    "current",
  )({ outcome: { kind: "SUCCEEDED", data: { items: [] } } });
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
  )({ outcome: { kind: "SUCCEEDED", data: { work: { basis: { businessDate: "2026-11-05" } } } } });
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
    )({ outcome: { kind: "SUCCEEDED", data: { ...data, businessDate: "2026-11-06" } } });
  }
  expect(observed).toHaveBeenCalledExactlyOnceWith("2026-11-05");
  const late = captureBusinessDateObserver("case.list", "current");
  close();
  late(response("2026-11-07"));
  expect(observed).toHaveBeenCalledTimes(1);
});

it("does not publish an aborted validated read after its transport eventually drains", async () => {
  const { service } = await import("../../../src/api/service");
  const { response: transportResponse } = await import("../../support/service-ui.fixtures");
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
  const fetch = vi.fn().mockReturnValue(transport);
  vi.stubGlobal("fetch", fetch);
  const controller = new AbortController();
  try {
    const read = service.list(null, 10, "current", controller.signal);
    await vi.waitFor(() => {
      expect(fetch).toHaveBeenCalledOnce();
    });
    controller.abort();
    finish(
      transportResponse("case.list", "SUCCEEDED", { items: [], offers: [], nextCursor: null }),
    );
    expect((await read).kind).toBe("deliveryFailure");
    expect(observed).not.toHaveBeenCalled();
  } finally {
    close();
    vi.unstubAllGlobals();
  }
});
