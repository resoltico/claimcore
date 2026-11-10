import { TestProviders } from "../support/presentation-test-support";
import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { useOperationObservation } from "../../src/hooks/useOperationObservation";
import { useSession } from "../../src/session/useSession";

import { deferredResponse } from "../support/presentation-state.fixtures";
import { operationId } from "../support/service-ui.fixtures";
import { endpointResponse } from "../support/transport-response.fixtures";

import { wireReply } from "../support/reply.fixtures";
const accepted = wireReply("operation.execute");
if (accepted.outcome.kind !== "ACCEPTED") {
  throw new Error("CaseOperationReceipt fixture absent.");
}
const receipt = accepted.outcome.document;
const unavailable = {
  kind: "human",
  advice: {
    message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
    action: "ASK_FOR_ACCESS",
  },
} as const;
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("coalesces observation and discards an older receipt after changing the operation ID", async () => {
  const first = deferredResponse();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockReturnValueOnce(first.promise);
  fetch.mockResolvedValueOnce(
    endpointResponse("operation.get", {
      representation: "HUMAN",
      outcome: { kind: "REJECTED", rejection: { kind: "REFUSED", advice: unavailable.advice } },
    }),
  );
  const { result } = renderHook(() => useOperationObservation(operationId), {
    wrapper: TestProviders,
  });
  let old: Promise<void> = Promise.resolve();
  act(() => {
    old = result.current.observe();
    void result.current.observe();
  });
  expect(fetch).toHaveBeenCalledOnce();
  act(() => {
    result.current.setOperationId("00000000-0000-4000-8000-000000000099");
  });
  expect(fetch.mock.calls[0]?.[1]?.signal?.aborted).toBe(true);
  await act(async () => {
    await result.current.observe();
  });
  expect(result.current.message).toEqual(unavailable);
  await act(async () => {
    first.resolve(
      endpointResponse("operation.get", {
        representation: "HUMAN",
        outcome: { kind: "ACCEPTED", document: receipt, byRequester: true },
      }),
    );
    await old;
  });
  expect(result.current.receipt).toBeNull();
  expect(result.current.message).toEqual(unavailable);
});

it("aborts observation on unmount without delivering a late receipt", async () => {
  const pending = deferredResponse();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockReturnValueOnce(pending.promise);
  const { result, unmount } = renderHook(() => useOperationObservation(operationId), {
    wrapper: TestProviders,
  });
  let task: Promise<void> = Promise.resolve();
  act(() => {
    task = result.current.observe();
  });
  unmount();
  expect(fetch.mock.calls[0]?.[1]?.signal?.aborted).toBe(true);
  await act(async () => {
    pending.resolve(
      endpointResponse("operation.get", {
        representation: "HUMAN",
        outcome: { kind: "ACCEPTED", document: receipt, byRequester: true },
      }),
    );
    await pending.promise;
    await task;
  });
  expect(result.current.receipt).toBeNull();
});

const snapshot = (authenticated: boolean, antiforgeryToken: string) => {
  const value = wireReply("session");
  if (value.outcome.kind !== "SUCCEEDED") throw new Error("Session fixture required");
  return endpointResponse("session", {
    outcome: { ...value.outcome, data: { ...value.outcome.data, authenticated, antiforgeryToken } },
  });
};

it("keeps the latest session snapshot when refresh responses finish in reverse order", async () => {
  const pending = deferredResponse();
  vi.mocked(globalThis.fetch)
    .mockReturnValueOnce(pending.promise)
    .mockResolvedValueOnce(snapshot(false, "newest"));
  const { result } = renderHook(() => useSession());
  await act(async () => {
    await result.current.refresh();
  });
  expect(result.current.state).toMatchObject({ kind: "anonymous", token: "newest" });
  await act(async () => {
    pending.resolve(snapshot(true, "older"));
    await pending.promise;
  });
  await waitFor(() => {
    expect(result.current.state).toMatchObject({ kind: "anonymous", token: "newest" });
  });
});

it("does not let an older refresh resurrect an authenticated session after logout", async () => {
  const pending = deferredResponse();
  vi.mocked(globalThis.fetch)
    .mockResolvedValueOnce(snapshot(true, "initial"))
    .mockReturnValueOnce(pending.promise)
    .mockResolvedValueOnce(endpointResponse("session.logout", wireReply("session.logout")));
  const { result } = renderHook(() => useSession());
  await waitFor(() => {
    expect(result.current.state.kind).toBe("authenticated");
  });
  let refresh: Promise<void> = Promise.resolve();
  act(() => {
    refresh = result.current.refresh();
  });
  await act(async () => {
    await result.current.logout();
  });
  await act(async () => {
    pending.resolve(snapshot(true, "obsolete"));
    await refresh;
  });
  expect(result.current.state).toMatchObject({ kind: "anonymous", token: null });
});
