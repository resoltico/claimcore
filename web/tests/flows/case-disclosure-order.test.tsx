import { timelinePage, timelineEntry } from "../support/timeline.fixtures";
import { caseViewInput } from "../support/inputs";
import { caseAdvice } from "../support/case-advice.fixtures";
import userEvent from "@testing-library/user-event";
import { act } from "@testing-library/react";
import { expect, it, vi } from "vitest";
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
const history = (nextCursor: string | null = null, page = 1) =>
  response(
    "case.timeline",
    "SUCCEEDED",
    timelinePage(
      [timelineEntry({ position: { sequence: page, entryHash: String(page).repeat(64) } })],
      nextCursor,
    ),
  );
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
        name: "Activity",
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
      name: "Activity",
    }),
  ).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(1);
});
for (const replacement of ["session", "refresh"]) {
  it(`discards earlier history while draining when ${replacement} current advice is refused`, async () => {
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
    expect(oldSignal?.aborted).toBe(false);
    await act(async () => {
      nextCurrent.release(response("case.get", "REJECTED", unavailable));
      oldHistory.release(history());
      await Promise.all([nextCurrent.promise, oldHistory.promise]);
    });
    await screen.findByRole("alert");
    expect(
      screen.queryByRole("heading", {
        name: "Activity",
      }),
    ).toBeNull();
    expect(screen.queryByText(fields.registration.claimantName)).toBeNull();
    expect(fetch).toHaveBeenCalledTimes(3);
  });
}
it("keeps authorized history pagination and discards a draining page on session replacement", async () => {
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
      name: "Load 50 more",
    }),
  );
  expect(document.querySelectorAll(".case-timeline li")).toHaveLength(1);
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
  expect(document.querySelectorAll(".case-timeline li")).toHaveLength(0);
  expect(signal?.aborted).toBe(false);
  await act(async () => {
    nextPage.release(history());
    await nextPage.promise;
  });
  await screen.findByRole("alert");
  expect(document.querySelectorAll(".case-timeline li")).toHaveLength(0);
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
      name: "Load 50 more",
    }),
  );
  await waitFor(() => {
    expect(document.querySelectorAll(".case-timeline li")).toHaveLength(2);
  });
  expect(screen.getAllByText(fields.registration.claimantName)[0]).toBeVisible();
  expect(
    screen.queryByRole("button", {
      name: "Load 50 more",
    }),
  ).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(3);
});
