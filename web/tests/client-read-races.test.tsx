import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { useOperationObservation } from "../src/hooks/useOperationObservation";
import { useSession } from "../src/hooks/useSession";

import { deferredResponse } from "./presentation-state.fixtures";
import { operationId, response, unavailable } from "./service-ui.fixtures";

import { wireReply } from "./contract-corpus.fixtures";
const accepted = wireReply("operation.execute");
if (accepted.outcome.tag !== "ACCEPTED") {
  throw new Error("CaseReceipt fixture absent.");
}
const receipt = accepted.outcome.data;
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("coalesces observation and discards an older receipt after changing the operation ID", async () => {
  const first = deferredResponse();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockReturnValueOnce(first.promise);
  fetch.mockResolvedValueOnce(response("operation.observe", "REJECTED", unavailable));
  const { result } = renderHook(() => useOperationObservation("token", operationId));
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
  expect(result.current.message).toEqual({
    kind: "diagnostic",
    diagnostic: { code: unavailable.code, limits: unavailable.limits },
  });
  await act(async () => {
    first.resolve(response("operation.observe", "SUCCEEDED", receipt));
    await old;
  });
  expect(result.current.receipt).toBeNull();
  expect(result.current.message).toEqual({
    kind: "diagnostic",
    diagnostic: { code: unavailable.code, limits: unavailable.limits },
  });
});

it("aborts observation on unmount without delivering a late receipt", async () => {
  const pending = deferredResponse();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockReturnValueOnce(pending.promise);
  const { result, unmount } = renderHook(() => useOperationObservation("token", operationId));
  let task: Promise<void> = Promise.resolve();
  act(() => {
    task = result.current.observe();
  });
  unmount();
  expect(fetch.mock.calls[0]?.[1]?.signal?.aborted).toBe(true);
  pending.resolve(response("operation.observe", "SUCCEEDED", receipt));
  await task;
  expect(result.current.receipt).toBeNull();
});

const snapshot = (authenticated: boolean, antiforgeryToken: string) =>
  response("session", "SUCCEEDED", { authenticated, antiforgeryToken });

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
    .mockResolvedValueOnce(
      response("session.logout", "SUCCEEDED", {
        authenticated: false,
        antiforgeryToken: "logged-out",
      }),
    );
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
