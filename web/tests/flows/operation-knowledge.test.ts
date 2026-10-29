import { mockEndpoint } from "../support/endpoint-mocks";
import { act } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { service } from "../../src/api/service";
import { wireReply } from "../support/contract-corpus.fixtures";
import { hook, preview, rejected } from "../support/operation-hook.fixtures";
afterEach(() => vi.restoreAllMocks());
it.each([false, true])(
  "prior uncertainty survives later refusal including content conflict=%s",
  async (conflict) => {
    vi.spyOn(service, "preview").mockResolvedValue(preview());
    const execute = vi
      .spyOn(service, "execute")
      .mockResolvedValueOnce({
        kind: "deliveryFailure",
        notice: { kind: "local", reason: "unreachable" },
      })
      .mockResolvedValue(rejected(conflict));
    const view = hook();
    await act(async () => {
      await view.result.current.preview();
    });
    act(() => {
      view.result.current.confirm(true);
    });
    await act(async () => {
      await view.result.current.execute();
    });
    const { prepared } = view.result.current;
    await act(async () => {
      await view.result.current.execute();
    });
    expect(view.result.current.priorUncertain).toBe(true);
    expect(view.result.current.prepared).toBe(prepared);
    expect(execute.mock.calls[1]![0]).toBe(execute.mock.calls[0]![0]);
  },
);
it("exact preview replay accepts without executing and an observation never resolves a pending attempt", async () => {
  const accepted = wireReply("operation.execute");
  if (accepted.outcome.kind !== "ACCEPTED") {
    throw new Error("Missing acceptance fixture.");
  }
  vi.spyOn(service, "preview").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      operationId: accepted.operationId,
      outcome: { kind: "ALREADY_ACCEPTED", data: accepted.outcome.data },
    },
  });
  const execute = vi.spyOn(service, "execute");
  const view = hook();
  await act(async () => {
    await view.result.current.preview();
  });
  expect(view.result.current.phase).toBe("ACCEPTED");
  expect(view.result.current.priorUncertain).toBe(false);
  expect(execute).not.toHaveBeenCalled();
});
it("preview conflict cannot execute or create uncertainty", async () => {
  const rejection = rejected(true);
  if (rejection.kind !== "outcome" || rejection.value.outcome.kind !== "REJECTED") {
    throw new Error("Missing refusal fixture.");
  }
  vi.spyOn(service, "preview").mockResolvedValue({
    ...rejection,
    value: { ...rejection.value, outcome: rejection.value.outcome },
  });
  const execute = vi.spyOn(service, "execute");
  const view = hook();
  await act(async () => {
    await view.result.current.preview();
  });
  act(() => {
    view.result.current.confirm(true);
  });
  await act(async () => {
    await view.result.current.execute();
  });
  expect(view.result.current.phase).toBe("EDITING");
  expect(view.result.current.priorUncertain).toBe(false);
  expect(execute).not.toHaveBeenCalled();
});
it("operation observation cannot change a pending editor", async () => {
  vi.spyOn(service, "preview").mockResolvedValue(preview());
  vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
  });
  mockEndpoint("operation.observe").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: wireReply("operation.observe"),
  });
  const view = hook();
  await act(async () => {
    await view.result.current.preview();
  });
  act(() => {
    view.result.current.confirm(true);
  });
  await act(async () => {
    await view.result.current.execute();
  });
  const { prepared } = view.result.current;
  await act(async () => {
    await service.observe(prepared!.input.operationId, "token");
  });
  expect(view.result.current.priorUncertain).toBe(true);
  expect(view.result.current.prepared).toBe(prepared);
});
