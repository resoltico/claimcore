import { caseViewInput } from "../support/inputs";
import { caseAdvice } from "../support/case-advice.fixtures";
import userEvent from "@testing-library/user-event";
import { historyVariants } from "../support/contract-corpus.fixtures";
import { act } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { HistoryEntry } from "../../src/api/service";
import { CaseDetail } from "../../src/views/CaseDetail";
import { render, screen, TestSession, waitFor } from "../support/presentation-test-support";
import { definition, fields, response, unavailable } from "../support/service-ui.fixtures";
const deferred = () => {
  let release!: (value: Response) => void;
  const promise = new Promise<Response>((resolve) => {
    release = resolve;
  });
  return {
    promise,
    release,
  };
};
const detail = (token = "token", reloadSignal = 0) => (
  <TestSession credential={token}>
    <CaseDetail
      target={{ kind: "REFERENCE", caseReference: fields.caseReference }}
      reloadSignal={reloadSignal}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />
  </TestSession>
);
const readable = (historyDetails: ReadonlyArray<"SUMMARY" | "FULL"> = ["SUMMARY", "FULL"]) =>
  response(
    "case.get",
    "SUCCEEDED",
    caseViewInput({
      caseId: "20000000-0000-4000-8000-000000000001",
      caseReference: fields.caseReference,
      revision: 1,
      advice: {
        ...caseAdvice([]),
        history: historyDetails,
      },
      state: {
        kind: "ACTIVE",
        fields,
      },
    }),
  );
const pageEntry = (
  entry: Extract<
    HistoryEntry,
    {
      kind: "SUMMARY";
    }
  >,
  page: number,
) => ({
  kind: entry.kind,
  change: {
    ...entry.change,
    operationId: `10000000-0000-4000-8000-00000000000${page}`,
  },
});
const history = (nextCursor: string | null = null, page = 1) => {
  const { summary } = historyVariants();
  const entries = [pageEntry(summary, page)];
  return response("case.history", "SUCCEEDED", {
    entries,
    nextCursor,
  });
};
const refusal = (kind: string) =>
  kind === "access"
    ? response("case.get", "REJECTED", unavailable)
    : new Response(
        JSON.stringify({
          hostFailure: {
            code: "SESSION_REJECTED",
            phase: "NOT_STARTED",
          },
        }),
        {
          status: 401,
          headers: {
            "content-type": "application/json",
          },
        },
      );
it.each(["access", "session"])(
  "does not launch history before current advice or after %s refusal",
  async (kind) => {
    const current = deferred();
    const oldHistory = deferred();
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockImplementation((url) =>
        typeof url === "string" && url.endsWith("case.get") ? current.promise : oldHistory.promise,
      );
    vi.stubGlobal("fetch", fetch);
    render(detail());
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledTimes(1);
    });
    await act(async () => {
      current.release(refusal(kind));
      await current.promise;
    });
    await screen.findByRole("alert");
    await act(async () => {
      oldHistory.release(history());
      await oldHistory.promise;
    });
    expect(
      screen.queryByRole("heading", {
        name: "Case history",
      }),
    ).toBeNull();
    expect(fetch).toHaveBeenCalledTimes(1);
  },
);
it("does not invent SUMMARY access for a readable case without history advice", async () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(readable([]));
  vi.stubGlobal("fetch", fetch);
  render(detail());
  await screen.findAllByText(fields.registration.claimantName);
  expect(
    screen.queryByRole("heading", {
      name: "Case history",
    }),
  ).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(1);
});
for (const replacement of ["session", "refresh"]) {
  it(`aborts earlier history when ${replacement} current advice is refused`, async () => {
    const oldHistory = deferred();
    const nextCurrent = deferred();
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(readable())
      .mockReturnValueOnce(oldHistory.promise)
      .mockReturnValueOnce(nextCurrent.promise);
    vi.stubGlobal("fetch", fetch);
    const view = render(detail());
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledTimes(2);
    });
    const oldSignal = fetch.mock.calls[1]?.[1]?.signal;
    view.rerender(
      detail(
        replacement === "session" ? "replacement" : "token",
        replacement === "refresh" ? 1 : 0,
      ),
    );
    expect(screen.queryByText(fields.registration.claimantName)).toBeNull();
    expect(oldSignal?.aborted).toBe(true);
    await act(async () => {
      nextCurrent.release(response("case.get", "REJECTED", unavailable));
      oldHistory.release(history());
      await Promise.all([nextCurrent.promise, oldHistory.promise]);
    });
    await screen.findByRole("alert");
    expect(
      screen.queryByRole("heading", {
        name: "Case history",
      }),
    ).toBeNull();
    expect(screen.queryByText(fields.registration.claimantName)).toBeNull();
    expect(fetch).toHaveBeenCalledTimes(3);
  });
}
it("keeps authorized history pagination and aborts a pending page on session replacement", async () => {
  const nextPage = deferred();
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(readable())
    .mockResolvedValueOnce(history("continuation"))
    .mockReturnValueOnce(nextPage.promise)
    .mockResolvedValueOnce(refusal("session"));
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  const view = render(detail());
  await user.click(
    await screen.findByRole("button", {
      name: "Load more history",
    }),
  );
  expect(document.querySelectorAll(".history-list li")).toHaveLength(1);
  const signal = fetch.mock.calls[2]?.[1]?.signal;
  const body = fetch.mock.calls[2]?.[1]?.body;
  if (typeof body !== "string") {
    throw new Error("History request body is required.");
  }
  const request = JSON.parse(body) as {
    cursor: string;
  };
  expect(request.cursor).toBe("continuation");
  view.rerender(detail("replacement"));
  expect(document.querySelectorAll(".history-list li")).toHaveLength(0);
  expect(signal?.aborted).toBe(true);
  await act(async () => {
    nextPage.release(history());
    await nextPage.promise;
  });
  await screen.findByRole("alert");
  expect(document.querySelectorAll(".history-list li")).toHaveLength(0);
  expect(fetch).toHaveBeenCalledTimes(4);
});
it("appends an authorized next history page without dropping current advice", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(readable())
    .mockResolvedValueOnce(history("continuation"))
    .mockResolvedValueOnce(history(null, 2));
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  render(detail());
  await user.click(
    await screen.findByRole("button", {
      name: "Load more history",
    }),
  );
  await waitFor(() => {
    expect(document.querySelectorAll(".history-list li")).toHaveLength(2);
  });
  expect(screen.getAllByText(fields.registration.claimantName)[0]).toBeVisible();
  expect(
    screen.queryByRole("button", {
      name: "Load more history",
    }),
  ).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(3);
});
