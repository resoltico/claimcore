import { act, renderHook, waitFor } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { ApiResult, WebV2Response } from "../src/api/v2";
import { useRetryablePage, useV2Read } from "../src/hooks/useV2Read";

type ListResponse = WebV2Response<"case.list">;

const result = (reference: string): ApiResult<ListResponse> => ({
  kind: "outcome",
  value: {
    endpoint: "case.list",
    outcome: {
      tag: "SUCCEEDED",
      data: {
        items: [{ caseReference: reference, revision: "1", status: "OPENED" }],
        nextCursor: null,
      },
    },
  },
  status: 200,
});

const selectPage = (response: ListResponse) =>
  response.outcome.tag === "SUCCEEDED"
    ? {
        items: response.outcome.data.items.map((item) => item.caseReference),
        nextCursor: response.outcome.data.nextCursor,
      }
    : null;

const selectFirst = (response: ListResponse): { value: string } | null => {
  const page = selectPage(response);
  const value = page?.items[0];
  return value === undefined ? null : { value };
};

it("discards an aborted keyed read instead of replacing its newer response", async () => {
  let completeFirst: ((value: ApiResult<ListResponse>) => void) | undefined;
  const first = new Promise<ApiResult<ListResponse>>((resolve) => {
    completeFirst = resolve;
  });
  const request = vi.fn().mockReturnValueOnce(first).mockResolvedValueOnce(result("new"));
  const { result: hook, rerender } = renderHook(({ key }) => useV2Read(request, selectFirst, key), {
    initialProps: { key: "first" },
  });
  rerender({ key: "second" });
  completeFirst?.(result("old"));
  await waitFor(() => expect(hook.current.value).toEqual({ value: "new" }));
});

it("aborts a stale page load before appending a newer cursor page", async () => {
  let completeFirst: ((value: ApiResult<ListResponse>) => void) | undefined;
  const first = new Promise<ApiResult<ListResponse>>((resolve) => {
    completeFirst = resolve;
  });
  const request = vi.fn().mockReturnValueOnce(first).mockResolvedValueOnce(result("new"));
  const { result: hook } = renderHook(() => useRetryablePage(request, selectPage));
  await waitFor(() => expect(request).toHaveBeenCalledOnce());
  await act(async () => hook.current.load("next"));
  completeFirst?.(result("old"));
  await waitFor(() => expect(hook.current.items).toEqual(["new"]));
});

it("coalesces duplicate page loads for one in-flight cursor", async () => {
  let finish: ((value: ApiResult<ListResponse>) => void) | undefined;
  const pending = new Promise<ApiResult<ListResponse>>((resolve) => {
    finish = resolve;
  });
  const request = vi.fn(() => pending);
  const { result: hook } = renderHook(() => useRetryablePage(request, selectPage));
  await waitFor(() => expect(request).toHaveBeenCalledOnce());
  let first: Promise<void> = Promise.resolve();
  let second: Promise<void> = Promise.resolve();
  act(() => {
    first = hook.current.load(null);
    second = hook.current.load(null);
  });
  expect(first).toBe(second);
  expect(request).toHaveBeenCalledOnce();
  await act(async () => {
    finish?.(result("same"));
    await first;
  });
  expect(hook.current.items).toEqual(["same"]);
});
