import { act, renderHook, waitFor } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { ApiResult, CaseSummary, ServiceResponse } from "../src/api/service";
import { isDisclosureRefused } from "../src/api/outcomes";
import { localNotice } from "../src/protocol/notices";
import { useRetryablePage } from "../src/hooks/useRead";
type ListResponse = ServiceResponse<"case.list">;
const initial: ApiResult<ListResponse> = {
  kind: "outcome",
  status: 200,
  value: {
    outcome: {
      kind: "SUCCEEDED",
      data: {
        offers: [],
        items: [
          {
            state: {
              kind: "ACTIVE",
              status: "OPENED",
            },
            caseReference: "SYNTHETIC-PRIVATE",
            revision: 1,
          },
        ],
        nextCursor: "continuation",
      },
    },
  },
};
const select = (response: ListResponse) =>
  response.outcome.kind === "SUCCEEDED" ? response.outcome.data : null;
const unavailable: ApiResult<ListResponse> = {
  kind: "outcome",
  status: 200,
  value: {
    outcome: {
      kind: "REJECTED",
      data: {
        code: "ACCESS_RESOURCE_UNAVAILABLE",
        action: "NONE_REQUIRED",
        target: null,
        actualRevision: null,
        limits: {},
      },
    },
  },
};
const sessionRejected: ApiResult<ListResponse> = {
  kind: "hostFailure",
  status: 401,
  failure: {
    code: "SESSION_REJECTED",
    phase: "NOT_STARTED",
  },
};
const loadAfter = async (next: ApiResult<ListResponse>) => {
  const request = vi.fn().mockResolvedValueOnce(initial).mockResolvedValueOnce(next);
  const { result } = renderHook(() => useRetryablePage<ListResponse, CaseSummary>(request, select));
  await waitFor(() => {
    expect(result.current.items).toHaveLength(1);
    expect(result.current.cursor).toBe("continuation");
  });
  await act(async () => {
    await result.current.load("continuation");
  });
  return result;
};
it.each([
  {
    label: "resource grant",
    refusal: unavailable,
  },
  {
    label: "browser session",
    refusal: sessionRejected,
  },
])(
  "clears this page's cached rows cursor and metadata after a $label refusal",
  async ({ refusal }) => {
    const result = await loadAfter(refusal);
    expect(result.current.items).toEqual([]);
    expect(result.current.cursor).toBeNull();
    expect(result.current.page).toBeNull();
    expect(result.current.message).not.toBeNull();
  },
);
it("keeps a previously disclosed page retryable after a transient delivery failure", async () => {
  const result = await loadAfter({
    kind: "deliveryFailure",
    notice: localNotice("incomplete"),
  });
  expect(result.current.items).toHaveLength(1);
  expect(result.current.items[0]?.caseReference).toBe("SYNTHETIC-PRIVATE");
  expect(result.current.cursor).toBe("continuation");
  expect(result.current.page).not.toBeNull();
  expect(result.current.message).not.toBeNull();
});
it("does not interpret a local delivery notice as a definite server refusal", () => {
  expect(
    isDisclosureRefused({
      kind: "deliveryFailure",
      notice: {
        kind: "diagnostic",
        diagnostic: {
          code: "ACCESS_RESOURCE_UNAVAILABLE",
          limits: {},
        },
      },
    }),
  ).toBe(false);
});
it("resets an invalid continuation locally and waits for a deliberate fresh reload", async () => {
  const refused = vi.fn();
  const invalid: ApiResult<ListResponse> = {
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "REJECTED",
        data: {
          code: "QUERY_CURSOR_INVALID",
          action: "READ_CURRENT",
          target: "cursor",
          actualRevision: null,
          limits: {},
        },
      },
    },
  };
  const request = vi
    .fn()
    .mockResolvedValueOnce(initial)
    .mockResolvedValueOnce(invalid)
    .mockResolvedValueOnce(initial);
  const { result } = renderHook(() =>
    useRetryablePage<ListResponse, CaseSummary>(request, select, refused),
  );
  await waitFor(() => {
    expect(result.current.items).toHaveLength(1);
  });
  await act(async () => {
    await result.current.load("continuation");
  });
  expect(result.current.items).toEqual([]);
  expect(result.current.page).toBeNull();
  expect(result.current.cursor).toBeNull();
  expect(result.current.message).not.toBeNull();
  expect(refused).not.toHaveBeenCalled();
  expect(request).toHaveBeenCalledTimes(2);
  await act(async () => {
    await result.current.load(null);
  });
  expect(result.current.items).toHaveLength(1);
  expect(request.mock.calls[2]?.[0]).toBeNull();
  expect(refused).not.toHaveBeenCalled();
});
