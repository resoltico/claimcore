import { createInspectionControl } from "../src/hooks/recoveryControl";
import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { useOperationObservation } from "../src/hooks/useOperationObservation";
import { useSession } from "../src/hooks/useSession";
import { inspectOperation } from "../src/views/recovery/RecoveryState";
import { deferredResponse } from "./presentation-state.fixtures";
import { inspection, receipt, list } from "./v3-recovery.fixtures";
import { operationId, response, preparation } from "./v3-ui.fixtures";
import { RecoveryView } from "../src/views/RecoveryView";
import { render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("coalesces observation and discards an older receipt after changing the operation ID", async () => {
  const first = deferredResponse();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockReturnValueOnce(first.promise);
  fetch.mockResolvedValueOnce(
    response("operation.observe", "SUCCEEDED", {
      tag: "NOT_FOUND",
      operationId: "00000000-0000-4000-8000-000000000099",
    }),
  );
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
  expect(result.current.notObserved).toBe(true);
  await act(async () => {
    first.resolve(response("operation.observe", "SUCCEEDED", { tag: "FOUND", receipt }));
    await old;
  });
  expect(result.current.receipt).toBeNull();
  expect(result.current.notObserved).toBe(true);
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
  pending.resolve(response("operation.observe", "SUCCEEDED", { tag: "FOUND", receipt }));
  await task;
  expect(result.current.receipt).toBeNull();
});

const inspectionUi = () => ({
  inspection: createInspectionControl(),
  setSelected: vi.fn(),
  setMessage: vi.fn(),
  setBusy: vi.fn(),
});

it("discards reverse-order recovery inspection responses and cancels closed detail reads", async () => {
  const first = deferredResponse();
  const last = deferredResponse();
  const fetch = vi.mocked(globalThis.fetch);
  fetch
    .mockReturnValueOnce(first.promise)
    .mockResolvedValueOnce(inspection())
    .mockReturnValueOnce(last.promise);
  const ui = inspectionUi();
  const old = inspectOperation(operationId, null, "token", ui);
  await inspectOperation(operationId, "new-page", "token", ui);
  expect(ui.setSelected).toHaveBeenCalledTimes(2);
  first.resolve(
    response("recovery.inspect", "SUCCEEDED", { tag: "NOT_FOUND", identity: operationId }),
  );
  await old;
  expect(ui.setSelected).toHaveBeenCalledTimes(2);
  expect(ui.setMessage).toHaveBeenCalledOnce();
  const closed = inspectOperation(operationId, "last-page", "token", ui);
  ui.inspection.cancel();
  ui.setSelected(null);
  last.resolve(inspection());
  await closed;
  expect(ui.setSelected.mock.calls.at(-1)?.[0]).toBeNull();
  expect(fetch.mock.calls[2]?.[1]?.signal?.aborted).toBe(true);
});

it("keeps attempt paging cancellable and prevents a late page from reopening closed recovery details", async () => {
  const user = userEvent.setup();
  const pending = deferredResponse();
  const fetch = vi.mocked(globalThis.fetch);
  fetch
    .mockResolvedValueOnce(list())
    .mockResolvedValueOnce(
      response("recovery.inspect", "SUCCEEDED", {
        tag: "FOUND",
        value: {
          tag: "RETAINED",
          value: {
            preparation: { ...preparation, attempts: { items: [], nextCursor: "page-two" } },
            observation: { tag: "NOT_FOUND", identity: operationId },
          },
        },
      }),
    )
    .mockReturnValueOnce(pending.promise);
  render(<RecoveryView onRecovery={vi.fn()} onMutationLockChange={vi.fn()} token="token" />);
  await user.click(await screen.findByRole("button", { name: "Inspect" }));
  await user.click(await screen.findByRole("button", { name: "Load more attempts" }));
  expect(screen.getByRole("button", { name: "Resolve exact preparation" })).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Load more attempts" }));
  expect(fetch).toHaveBeenCalledTimes(3);
  await user.click(screen.getByRole("button", { name: "Close inspection" }));
  expect(fetch.mock.calls[2]?.[1]?.signal?.aborted).toBe(true);
  await act(async () => {
    pending.resolve(inspection());
    await pending.promise;
  });
  expect(screen.queryByRole("dialog")).toBeNull();
});

const snapshot = (authenticated: boolean, antiforgeryToken: string) =>
  response("session", "SNAPSHOT", { authenticated, antiforgeryToken });

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
      response("session.logout", "SNAPSHOT", {
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
  expect(result.current.state).toMatchObject({ kind: "anonymous", token: "logged-out" });
});
