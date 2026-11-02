import { workBasis, caseSummaryInput } from "../../support/inputs";
import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import * as api from "../../../src/api/service";
import type { ApiResult, ServiceResponse } from "../../../src/api/service";
import { useEndpoint } from "../../../src/hooks/useEndpoint";
import { useEndpointPage } from "../../../src/hooks/useEndpointPage";
import { TestProviders } from "../../support/presentation-test-support";
type ListResponse = ServiceResponse<"case.list">;
const result = (reference: string): ApiResult<ListResponse> => ({
  kind: "outcome",
  value: {
    outcome: {
      kind: "SUCCEEDED",
      data: {
        basis: workBasis,
        offers: [],
        items: [
          caseSummaryInput({
            state: { kind: "ACTIVE", status: "OPENED" },
            caseId: "20000000-0000-4000-8000-000000000001",
            caseReference: reference,
            revision: 1,
          }),
        ],
        nextCursor: null,
      },
    },
  },
  status: 200,
});
const selectPage = (response: ListResponse) =>
  response.outcome.kind === "SUCCEEDED"
    ? {
        items: response.outcome.data.items.map((item) => item.caseReference),
        nextCursor: response.outcome.data.nextCursor,
      }
    : null;
const pageInput = (cursor: string | null) => ({
  limit: 50,
  ...(cursor === null ? {} : { cursor }),
});
const connect = (
  request: (cursor: string | null, signal: AbortSignal) => Promise<ApiResult<ListResponse>>,
) =>
  vi
    .spyOn(api, "requestEndpoint")
    .mockImplementation((_endpoint, _credential, body, signal) =>
      request("cursor" in body ? (body.cursor ?? null) : null, signal!),
    );
afterEach(() => vi.restoreAllMocks());
it("discards an aborted keyed read instead of replacing its newer response", async () => {
  let finish: (value: ApiResult<ListResponse>) => void = () => undefined;
  const first = new Promise<ApiResult<ListResponse>>((resolve) => {
    finish = resolve;
  });
  connect(
    vi
      .fn<(cursor: string | null, signal: AbortSignal) => Promise<ApiResult<ListResponse>>>()
      .mockReturnValueOnce(first)
      .mockResolvedValueOnce(result("new")),
  );
  const view = renderHook(({ cursor }) => useEndpoint("case.list", { limit: 50, cursor }), {
    initialProps: { cursor: "first" },
    wrapper: TestProviders,
  });
  view.rerender({ cursor: "second" });
  finish(result("old"));
  await waitFor(() => {
    expect(
      view.result.current.value?.outcome.kind === "SUCCEEDED"
        ? selectPage(view.result.current.value)?.items
        : null,
    ).toEqual(["new"]);
  });
});
it("aborts a stale page load before appending a newer cursor page", async () => {
  let finish: (value: ApiResult<ListResponse>) => void = () => undefined;
  const first = new Promise<ApiResult<ListResponse>>((resolve) => {
    finish = resolve;
  });
  const request = vi
    .fn<(cursor: string | null, signal: AbortSignal) => Promise<ApiResult<ListResponse>>>()
    .mockReturnValueOnce(first)
    .mockResolvedValueOnce(result("new"));
  connect(request);
  const view = renderHook(
    () =>
      useEndpointPage<"case.list", string, NonNullable<ReturnType<typeof selectPage>>>(
        "case.list",
        pageInput,
        selectPage,
      ),
    { wrapper: TestProviders },
  );
  await waitFor(() => {
    expect(request).toHaveBeenCalledOnce();
  });
  await act(async () => {
    await view.result.current.load("next");
  });
  finish(result("old"));
  await waitFor(() => {
    expect(view.result.current.items).toEqual(["new"]);
  });
});
it("coalesces duplicate page loads for one in-flight cursor", async () => {
  let finish: (value: ApiResult<ListResponse>) => void = () => undefined;
  const pending = new Promise<ApiResult<ListResponse>>((resolve) => {
    finish = resolve;
  });
  const request = vi.fn(() => pending);
  connect(request);
  const view = renderHook(
    () =>
      useEndpointPage<"case.list", string, NonNullable<ReturnType<typeof selectPage>>>(
        "case.list",
        pageInput,
        selectPage,
      ),
    { wrapper: TestProviders },
  );
  await waitFor(() => {
    expect(request).toHaveBeenCalledOnce();
  });
  let first = Promise.resolve();
  let second = Promise.resolve();
  act(() => {
    first = view.result.current.load(null);
    second = view.result.current.load(null);
  });
  expect(first).toBe(second);
  expect(request).toHaveBeenCalledOnce();
  await act(async () => {
    finish(result("same"));
    await first;
  });
  expect(view.result.current.items).toEqual(["same"]);
});
it("starts a changed read input at the same cursor while its cancelled predecessor is pending", async () => {
  let finish: (value: ApiResult<ListResponse>) => void = () => undefined;
  const first = new Promise<ApiResult<ListResponse>>((resolve) => {
    finish = resolve;
  });
  const request = vi
    .fn<(cursor: string | null, signal: AbortSignal) => Promise<ApiResult<ListResponse>>>()
    .mockReturnValueOnce(first)
    .mockResolvedValueOnce(result("full"));
  connect(request);
  const initial = (cursor: string | null) => ({ ...pageInput(cursor), limit: 50 });
  const changed = (cursor: string | null) => ({ ...pageInput(cursor), limit: 25 });
  const view = renderHook(
    ({ input }) =>
      useEndpointPage<"case.list", string, NonNullable<ReturnType<typeof selectPage>>>(
        "case.list",
        input,
        selectPage,
      ),
    { initialProps: { input: initial }, wrapper: TestProviders },
  );
  await waitFor(() => {
    expect(request).toHaveBeenCalledOnce();
  });
  view.rerender({ input: changed });
  await waitFor(() => {
    expect(view.result.current.items).toEqual(["full"]);
  });
  expect(request.mock.calls[0]?.[1].aborted).toBe(true);
  await act(async () => {
    finish(result("obsolete"));
    await first;
  });
  expect(view.result.current.items).toEqual(["full"]);
});
it("does not start a queued initial page read after its owner unmounts", async () => {
  const request = vi.fn((_cursor: string | null, _signal: AbortSignal) =>
    Promise.resolve(result("fresh")),
  );
  connect(request);
  const usePage = () =>
    useEndpointPage<"case.list", string, NonNullable<ReturnType<typeof selectPage>>>(
      "case.list",
      pageInput,
      selectPage,
    );
  const removed = renderHook(usePage, { wrapper: TestProviders });
  removed.unmount();
  await act(() => Promise.resolve());
  expect(request).not.toHaveBeenCalled();
  const mounted = renderHook(usePage, { wrapper: TestProviders });
  await waitFor(() => {
    expect(mounted.result.current.items).toHaveLength(1);
  });
  mounted.unmount();
  expect(request.mock.calls[0]?.[1].aborted).toBe(true);
});
