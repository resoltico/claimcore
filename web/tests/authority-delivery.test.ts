import { act, renderHook } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { useAuthorityOperation } from "../src/hooks/operation/useAuthorityOperation";
import { service } from "../src/api/service";
import { generatedWebValue } from "./contract-corpus.fixtures";
const preparedHook = () => {
  const view = renderHook(({ token }) => useAuthorityOperation(token, "REGISTER_ACTOR", vi.fn()), {
    initialProps: { token: "first" },
  });
  act(() => {
    view.result.current.setValues({
      operationId: "10000000-0000-4000-8000-000000000001",
      "target.kind": "SERVICE",
      "target.issuer": "https://issuer.example/",
      "target.subject": "synthetic-service",
    });
  });
  act(() => {
    view.result.current.review();
  });
  act(() => {
    view.result.current.setConfirmed(true);
  });
  return view;
};
it("coalesces authority execution before React commits busy state", async () => {
  let finish: ((value: Awaited<ReturnType<typeof service.authorityExecute>>) => void) | undefined;
  const pending = new Promise<Awaited<ReturnType<typeof service.authorityExecute>>>((resolve) => {
    finish = resolve;
  });
  const send = vi.spyOn(service, "authorityExecute").mockReturnValue(pending);
  const view = preparedHook();
  let first: Promise<void> = Promise.resolve();
  act(() => {
    first = view.result.current.execute();
    void view.result.current.execute();
  });
  expect(send).toHaveBeenCalledOnce();
  await act(async () => {
    finish?.({ kind: "outcome", status: 200, value: generatedWebValue("authority.execute") });
    await first;
  });
  expect(view.result.current.uncertain).toBe(false);
  expect(view.result.current.prepared).toBeNull();
  expect(view.result.current.values["operationId"]).not.toBe(
    "10000000-0000-4000-8000-000000000001",
  );
  send.mockRestore();
});
it("ignores an authority completion from a superseded session context", async () => {
  let finish: ((value: Awaited<ReturnType<typeof service.authorityExecute>>) => void) | undefined;
  const pending = new Promise<Awaited<ReturnType<typeof service.authorityExecute>>>((resolve) => {
    finish = resolve;
  });
  const send = vi.spyOn(service, "authorityExecute").mockReturnValue(pending);
  const view = preparedHook();
  let first: Promise<void> = Promise.resolve();
  act(() => {
    first = view.result.current.execute();
  });
  view.rerender({ token: "second" });
  expect(view.result.current.uncertain).toBe(true);
  await act(async () => {
    finish?.({ kind: "deliveryFailure", notice: { kind: "local", reason: "unreachable" } });
    await first;
  });
  expect(view.result.current.uncertain).toBe(true);
  expect(view.result.current.receipt).toBeNull();
  send.mockRestore();
});
const retryDelivery = (next: "failed" | "conflict" | "replay") => {
  const accepted = generatedWebValue("authority.execute");
  if (accepted.outcome.tag !== "ACCEPTED") {
    throw new Error("Missing receipt fixture");
  }
  const send = vi.spyOn(service, "authorityExecute").mockResolvedValueOnce({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
  });
  if (next === "replay") {
    send.mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: { ...accepted, outcome: { tag: "ALREADY_ACCEPTED", data: accepted.outcome.data } },
    });
  } else {
    send.mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        ...accepted,
        outcome: {
          tag: "REJECTED",
          data: {
            diagnostic: {
              id:
                next === "conflict" ? "OPERATION_CONTENT_CONFLICT" : "ACCESS_RESOURCE_UNAVAILABLE",
              parameters: {},
            },
            message: "Synthetic refusal",
            field: null,
            actualRevision: null,
            recommendedAction: "NONE_REQUIRED",
          },
        },
      },
    });
  }
  return send;
};
it.each(["failed", "conflict", "replay"] as const)(
  "authority retry folds %s evidence without changing pending bytes",
  async (next) => {
    const send = retryDelivery(next);
    const view = preparedHook();
    await act(async () => {
      await view.result.current.execute();
    });
    expect(view.result.current.uncertain).toBe(true);
    const pending = view.result.current.prepared;
    await act(async () => {
      await view.result.current.execute();
    });
    expect(view.result.current.uncertain).toBe(next === "failed");
    expect(send.mock.calls[1]![0]).toBe(pending);
    expect(view.result.current.prepared).toBe(next === "replay" ? null : pending);
    send.mockRestore();
  },
);
it("operation lookup cannot resolve pending authority content", async () => {
  const send = vi.spyOn(service, "authorityExecute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
  });
  const observe = vi.spyOn(service, "observe").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: generatedWebValue("operation.observe"),
  });
  const view = preparedHook();
  await act(async () => {
    await view.result.current.execute();
  });
  const pending = view.result.current.prepared;
  await act(async () => {
    await service.observe(pending!.input.operationId, "first");
  });
  expect(view.result.current.uncertain).toBe(true);
  expect(view.result.current.prepared).toBe(pending);
  send.mockRestore();
  observe.mockRestore();
});
it("never discards an authority identity while dispatching or unresolved", async () => {
  let finish: ((value: Awaited<ReturnType<typeof service.authorityExecute>>) => void) | undefined;
  const send = vi.spyOn(service, "authorityExecute").mockImplementation(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  const view = preparedHook();
  const pending = view.result.current.prepared;
  let task = Promise.resolve();
  act(() => {
    task = view.result.current.execute();
    view.result.current.discard();
  });
  expect(view.result.current.prepared).toBe(pending);
  await act(async () => {
    finish?.({ kind: "deliveryFailure", notice: { kind: "local", reason: "unreachable" } });
    await task;
  });
  act(() => {
    view.result.current.discard();
  });
  expect(view.result.current.prepared).toBe(pending);
  expect(view.result.current.values["operationId"]).toBe(pending!.input.operationId);
  expect(view.result.current.uncertain).toBe(true);
  send.mockRestore();
});
