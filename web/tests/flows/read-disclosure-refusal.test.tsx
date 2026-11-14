import { humanReadReply } from "../support/human-read-reply.fixtures";
import { workBasis } from "../support/inputs";
import { act, renderHook, waitFor } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { ApiResult, ServiceResponse } from "../../src/api/service";
import { isDisclosureRefused } from "../../src/api/outcomes";
import { localNotice } from "../../src/protocol/notices";
import { useEndpointPage } from "../../src/hooks/useEndpointPage";
import * as api from "../../src/api/service";
import { TestProviders } from "../support/presentation-test-support";
const pageInput = (cursor: string | null) => ({
  limit: 50,
  ...(cursor === null ? {} : { cursor }),
});
const connect = (
  request: (cursor: string | null, signal: AbortSignal) => Promise<ApiResult<ListResponse>>,
) => {
  vi.spyOn(api, "requestEndpoint").mockImplementation((_endpoint, _credential, body, signal) =>
    request("cursor" in body ? (body.cursor ?? null) : null, signal!),
  );
};
type ListResponse = ServiceResponse<"case.list">;
const initial: ApiResult<ListResponse> = {
  kind: "outcome",
  status: 200,
  value: {
    ...humanReadReply("case.list", {
      screen: {
        regions: [
          {
            role: "RESULTS",
            content: {
              kind: "TABLE",
              columns: [
                {
                  key: "reference",
                  labelId: "field.caseReference.label",
                  prominence: "PRIMARY",
                  isMoney: false,
                },
              ],
              rows: [
                {
                  key: "20000000-0000-4000-8000-000000000001",
                  link: {
                    endpoint: "case.get",
                    input: {
                      target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
                    },
                  },
                  values: [{ kind: "REFERENCE", value: "SYNTHETIC-PRIVATE" }],
                },
              ],
            },
          },
        ],
      },
      subject: { kind: "LIST" },
      actions: [],
      links: [],
      history: null,
      nextCursor: "continuation",
      basis: { kind: "SNAPSHOT", observation: workBasis },
      disclosure: "CURRENT",
    }),
  },
};
const select = (response: ListResponse) => {
  if (response.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const doc = response.outcome.document;
  if (doc.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    items: doc.screen.regions.flatMap((region) =>
      region.content.kind === "TABLE" ? region.content.rows : [],
    ),
    nextCursor: doc.nextCursor,
    basis: doc.basis.observation,
    screen: doc.screen,
    disclosure: doc.disclosure,
  };
};
type CaseControl = NonNullable<ReturnType<typeof select>>["items"][number];
const unavailable: ApiResult<ListResponse> = {
  kind: "outcome",
  status: 200,
  value: {
    representation: "HUMAN",
    outcome: {
      kind: "REJECTED",
      rejection: {
        kind: "REFUSED",
        advice: {
          message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
          action: "ASK_FOR_ACCESS",
        },
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
  connect(request);
  const { result } = renderHook(
    () =>
      useEndpointPage<"case.list", CaseControl, NonNullable<ReturnType<typeof select>>>(
        "case.list",
        pageInput,
        select,
      ),
    { wrapper: TestProviders },
  );
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
  expect(result.current.items[0]?.key).toBe("20000000-0000-4000-8000-000000000001");
  expect(JSON.stringify(result.current.views)).toContain("SYNTHETIC-PRIVATE");
  expect(result.current.cursor).toBe("continuation");
  expect(result.current.page).not.toBeNull();
  expect(result.current.message).not.toBeNull();
});
it("does not interpret a local delivery notice as a definite server refusal", () => {
  expect(
    isDisclosureRefused({
      kind: "deliveryFailure",
      notice: {
        kind: "local",
        reason: "resourceUnavailable",
      },
    }),
  ).toBe(false);
});
const invalid: ApiResult<ListResponse> = {
  kind: "outcome",
  status: 200,
  value: {
    representation: "HUMAN",
    outcome: {
      kind: "REJECTED",
      rejection: {
        kind: "REFUSED",
        advice: {
          message: { messageId: "diagnostic.INVALID_CURSOR", arguments: [] },
          action: "READ_CURRENT",
        },
      },
    },
  },
};

it("resets an invalid continuation locally and waits for a deliberate fresh reload", async () => {
  const refused = vi.fn();
  const request = vi
    .fn()
    .mockResolvedValueOnce(initial)
    .mockResolvedValueOnce(invalid)
    .mockResolvedValueOnce(initial);
  connect(request);
  const { result } = renderHook(
    () =>
      useEndpointPage<"case.list", CaseControl, NonNullable<ReturnType<typeof select>>>(
        "case.list",
        pageInput,
        select,
        refused,
      ),
    { wrapper: TestProviders },
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
