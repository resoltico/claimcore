import { caseSummaryInput } from "../../support/inputs";
import { act, fireEvent, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { CaseList } from "../../../src/views/CaseList";
import { navigate } from "../../../src/routing/navigation";
import { context, response, unavailable } from "../../support/service-ui.fixtures";
// The list unit exercises page advice and cursors; summary loading is covered by SummaryBand's own tests.
vi.mock("../../../src/views/worklist/SummaryBand", () => ({ SummaryBand: () => null }));
const page = (mayOpen: boolean, nextCursor: string | null = "next") =>
  response("case.list", "SUCCEEDED", {
    items: [
      caseSummaryInput({
        state: { kind: "ACTIVE", status: "OPENED" },
        caseId: "20000000-0000-4000-8000-000000000001",
        caseReference: "ADVISED-CASE",
        revision: 1,
      }),
    ],
    offers: mayOpen ? context.offers : [],
    nextCursor,
  });
afterEach(() => vi.unstubAllGlobals());
it("opens the core-supplied exact reference without inferring it from the query or row count", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      response("case.list", "SUCCEEDED", {
        items: [
          caseSummaryInput({ caseReference: "OTHER-A" }),
          caseSummaryInput({
            caseReference: "OTHER-B",
            caseId: "20000000-0000-4000-8000-000000000002",
          }),
        ],
        exactReference: {
          caseId: "20000000-0000-4000-8000-000000000001",
          caseReference: "ADVISED-CASE",
        },
        offers: [],
        nextCursor: null,
      }),
    ),
  );
  navigate("/cases?q=different", true);
  const select = vi.fn();
  render(<CaseList onSelect={select} />);
  fireEvent.click(await screen.findByRole("button", { name: "Open ADVISED-CASE" }));
  expect(select).toHaveBeenCalledWith({
    kind: "ID",
    caseId: "20000000-0000-4000-8000-000000000001",
  });
});
it("does not infer an exact match from a matching visible row when the core supplies null", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(page(false, null)));
  navigate("/cases?q=advised-case", true);
  render(<CaseList onSelect={vi.fn()} />);
  await screen.findByRole("link", { name: "ADVISED-CASE" });
  expect(screen.queryByRole("button", { name: "Open ADVISED-CASE" })).toBeNull();
});
it("does not expand non-ASCII search text into a reference identity", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      response("case.list", "SUCCEEDED", {
        items: [caseSummaryInput({ caseReference: "S" })],
        offers: [],
        nextCursor: null,
      }),
    ),
  );
  navigate("/cases?q=%C5%BF", true);
  render(<CaseList onSelect={vi.fn()} />);
  await screen.findByRole("link", { name: "S" });
  expect(screen.queryByRole("button", { name: "Open S" })).toBeNull();
});
it.each([
  ["UNDECIDED", "Undecided"],
  ["DECIDED_UNPAID", "Decided, unpaid"],
  ["CLOSED", "Closed"],
  ["VOIDED", "Voided"],
  ["ALL", "All"],
] as const)("uses the core view label and empty message for %s", async (view, label) => {
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValue(
        response("case.list", "SUCCEEDED", { items: [], offers: [], nextCursor: null }),
      ),
  );
  navigate(`/cases?view=${view}`, true);
  render(<CaseList onSelect={vi.fn()} />);
  await screen.findByText("No matching items.");
  expect(screen.getByRole("tab", { name: `${label} 0` })).toHaveAttribute("aria-selected", "true");
  expect(screen.queryByText("No accessible cases returned.")).toBeNull();
  expect(screen.queryByRole("button", { name: "Clear filters" })).toBeNull();
});
it("keeps case creation in the shell and masks rows while a fresh page loads", async () => {
  let finish: (value: Response) => void = () => undefined;
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(page(true))
    .mockImplementationOnce(
      () =>
        new Promise<Response>((resolve) => {
          finish = resolve;
        }),
    );
  vi.stubGlobal("fetch", fetch);
  render(<CaseList onSelect={vi.fn()} />);
  await screen.findByRole("link", { name: "ADVISED-CASE" });
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(fetch.mock.calls[0]?.[0]).toBe("/api/case.list");
  fireEvent.click(
    screen.getByRole("button", {
      name: "Reload cases",
    }),
  );
  expect(
    screen.queryByRole("link", {
      name: "ADVISED-CASE",
    }),
  ).toBeNull();
  expect(
    screen.queryByRole("button", {
      name: "Open a case",
    }),
  ).toBeNull();
  await act(() =>
    Promise.resolve().then(() => {
      finish(page(false, null));
    }),
  );
  await screen.findByRole("link", {
    name: "ADVISED-CASE",
  });
  expect(
    screen.queryByRole("button", {
      name: "Open a case",
    }),
  ).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(2);
});
it.each(["access", "session"])(
  "clears rows continuation and advice on a %s refusal",
  async (kind) => {
    const failure =
      kind === "access"
        ? response("case.list", "REJECTED", unavailable)
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
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValueOnce(page(true)).mockResolvedValueOnce(failure),
    );
    render(<CaseList onSelect={vi.fn()} />);
    fireEvent.click(
      await screen.findByRole("button", {
        name: "Load more cases",
      }),
    );
    await screen.findByRole("alert");
    expect(
      screen.queryByRole("link", {
        name: "ADVISED-CASE",
      }),
    ).toBeNull();
    expect(
      screen.queryByRole("button", {
        name: "Open a case",
      }),
    ).toBeNull();
    expect(
      screen.queryByRole("button", {
        name: "Load more cases",
      }),
    ).toBeNull();
  },
);
it("retains the last accepted rows after a request-local failure", async () => {
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValueOnce(page(true))
      .mockRejectedValueOnce(new Error("Synthetic transport failure")),
  );
  render(<CaseList onSelect={vi.fn()} />);
  fireEvent.click(
    await screen.findByRole("button", {
      name: "Load more cases",
    }),
  );
  await waitFor(() => {
    expect(screen.getByRole("alert")).toBeVisible();
  });
  expect(
    screen.getByRole("link", {
      name: "ADVISED-CASE",
    }),
  ).toBeVisible();
});
