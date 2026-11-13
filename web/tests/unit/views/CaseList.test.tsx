import { workBasis } from "../../support/inputs";
import { act, fireEvent, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { CaseList } from "../../../src/views/CaseList";
import { navigate } from "../../../src/routing/navigation";
import type { HumanReadDocument_caseList } from "@contracts/client";
import { humanReadHttpReply as response } from "../../support/human-read-reply.fixtures";
import { endpointResponse } from "../../support/transport-response.fixtures";
// The list unit exercises page advice and cursors; summary loading is covered by SummaryBand's own tests.
vi.mock("../../../src/views/worklist/SummaryBand", () => ({ SummaryBand: () => null }));
const listed: HumanReadDocument_caseList = {
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "caseReference",
              labelId: "field.caseReference.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: [
            {
              key: "20000000-0000-4000-8000-000000000001",
              values: [{ kind: "REFERENCE", value: "ADVISED-CASE" }],
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "CASES",
    cases: [{ caseId: "20000000-0000-4000-8000-000000000001", assignment: null }],
    tasks: [],
    exactCaseId: null,
    nextCursor: null,
  },
  basis: { kind: "SNAPSHOT", observation: workBasis },
  disclosure: "CURRENT",
};
const page = (mayOpen: boolean, nextCursor: string | null = "next") =>
  response("case.list", {
    ...listed,
    context: { ...listed.context, tasks: mayOpen ? ["CASE:OPEN"] : [], nextCursor },
  });
afterEach(() => vi.unstubAllGlobals());
it("opens the core-supplied exact reference without inferring it from the query or row count", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      response("case.list", {
        ...listed,
        screen: {
          regions: [
            {
              role: "RESULTS",
              content: {
                kind: "TABLE",
                columns: [
                  {
                    key: "caseReference",
                    labelId: "field.caseReference.label",
                    prominence: "PRIMARY",
                    isMoney: false,
                  },
                ],
                rows: [
                  {
                    key: "20000000-0000-4000-8000-000000000001",
                    values: [{ kind: "REFERENCE", value: "OTHER-A" }],
                  },
                  {
                    key: "20000000-0000-4000-8000-000000000002",
                    values: [{ kind: "REFERENCE", value: "OTHER-B" }],
                  },
                ],
              },
            },
            {
              role: "NOTICE",
              content: {
                kind: "ENTRIES",
                entries: [
                  {
                    key: "exactReference",
                    labelId: "field.caseReference.label",
                    prominence: "PRIMARY",
                    value: { kind: "REFERENCE", value: "ADVISED-CASE" },
                  },
                ],
              },
            },
          ],
        },
        context: {
          ...listed.context,
          cases: [
            ...listed.context.cases,
            { caseId: "20000000-0000-4000-8000-000000000002", assignment: null },
          ],
          exactCaseId: "20000000-0000-4000-8000-000000000001",
        },
      }),
    ),
  );
  navigate("/cases?q=different", true);
  const select = vi.fn();
  render(<CaseList onSelect={select} />);
  fireEvent.click(await screen.findByRole("link", { name: "ADVISED-CASE" }));
  expect(select).toHaveBeenCalledWith({
    kind: "ID",
    caseId: "20000000-0000-4000-8000-000000000001",
  });
});
it("does not infer an exact match from a matching visible row when the core supplies null", async () => {
  vi.stubGlobal("fetch", vi.fn<typeof globalThis.fetch>().mockResolvedValue(page(false, null)));
  navigate("/cases?q=advised-case", true);
  render(<CaseList onSelect={vi.fn()} />);
  await screen.findByRole("link", { name: "ADVISED-CASE" });
  expect(screen.queryByRole("button", { name: "Open ADVISED-CASE" })).toBeNull();
});
it("does not expand non-ASCII search text into a reference identity", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      response("case.list", {
        ...listed,
        screen: {
          regions: [
            {
              role: "RESULTS",
              content: {
                kind: "TABLE",
                columns: [
                  {
                    key: "caseReference",
                    labelId: "field.caseReference.label",
                    prominence: "PRIMARY",
                    isMoney: false,
                  },
                ],
                rows: [
                  {
                    key: "20000000-0000-4000-8000-000000000001",
                    values: [{ kind: "REFERENCE", value: "S" }],
                  },
                ],
              },
            },
          ],
        },
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
  ["DECIDED_UNPAID", "Decided, not fully paid"],
  ["CLOSED", "Closed"],
  ["VOIDED", "Voided"],
  ["ALL", "All"],
] as const)("uses the core view label and empty message for %s", async (view, label) => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      response("case.list", {
        ...listed,
        screen: {
          regions: [
            {
              role: "RESULTS",
              content: {
                kind: "TABLE",
                columns: [
                  {
                    key: "caseReference",
                    labelId: "field.caseReference.label",
                    prominence: "PRIMARY",
                    isMoney: false,
                  },
                ],
                rows: [],
                sections: [
                  {
                    titleId: "label.page",
                    entries: [
                      {
                        key: "shown",
                        labelId: "label.shown",
                        prominence: "SECONDARY",
                        value: { kind: "COUNT", value: 0 },
                      },
                    ],
                  },
                ],
              },
            },
          ],
        },
        context: { ...listed.context, cases: [] },
      }),
    ),
  );
  navigate(`/cases?view=${view}`, true);
  render(<CaseList onSelect={vi.fn()} />);
  await screen.findByText("Nothing to show in this view.");
  expect(screen.getAllByText("Nothing to show in this view.")).toHaveLength(1);
  expect(screen.queryByRole("heading", { name: "Page" })).toBeNull();
  expect(screen.queryByText("All results shown.")).toBeNull();
  expect(screen.getByRole("tab", { name: `${label} 0` })).toHaveAttribute("aria-selected", "true");
  expect(screen.queryByText("No cases to show.")).toBeNull();
  expect(screen.queryByRole("button", { name: "Clear filters" })).toBeNull();
});
it("keeps case creation in the shell and masks rows while a fresh page loads", async () => {
  let finish: (value: Response) => void = () => undefined;
  const fetch = vi
    .fn<typeof globalThis.fetch>()
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
        ? endpointResponse("case.list", {
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
          })
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
