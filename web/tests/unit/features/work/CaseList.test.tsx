import { workBasis } from "../../../support/inputs";
import { act, fireEvent, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../../../support/presentation-test-support";
import { CaseList } from "../../../../src/features/work/CaseList";
import { navigate } from "../../../../src/routing/navigation";
import type { HumanDocument } from "@contracts/client";
import { humanReadHttpReply as response } from "../../../support/human-read-reply.fixtures";
import { endpointResponse } from "../../../support/transport-response.fixtures";
// The list unit exercises page advice and cursors; summary loading is covered by SummaryBand's own tests.
vi.mock("../../../../src/features/work/worklist/SummaryBand", () => ({ SummaryBand: () => null }));
const listed: HumanDocument = {
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
              joins: null,
              isMoney: false,
            },
          ],
          rows: [
            {
              key: "20000000-0000-4000-8000-000000000001",
              link: {
                endpoint: "case.get",
                input: { target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" } },
              },
              values: [{ kind: "REFERENCE", value: "CC-260Q-KJ0K-1CGJ-Q3NP-85" }],
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
  nextCursor: null,
  basis: { kind: "SNAPSHOT", observation: workBasis },
  disclosure: "CURRENT",
};
const page = (mayOpen: boolean, nextCursor: string | null = "next") =>
  response("case.list", {
    ...listed,
    nextCursor,
    actions: listed.actions.filter((action) =>
      new Set<string>(mayOpen ? ["case.open"] : []).has(action.task),
    ),
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
                    joins: null,
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
                    values: [{ kind: "REFERENCE", value: "CC-MM05-2VZ7-6RD8-WR1R-78" }],
                  },
                  {
                    key: "20000000-0000-4000-8000-000000000002",
                    link: {
                      endpoint: "case.get",
                      input: {
                        target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000002" },
                      },
                    },
                    values: [{ kind: "REFERENCE", value: "CC-ED8K-XKRZ-D2WR-VSJF-59" }],
                  },
                ],
              },
            },
            {
              role: "FACTS",
              titleId: "label.exactReference",
              content: {
                kind: "ENTRIES",
                entries: [
                  {
                    key: "exactReference",
                    labelId: "field.caseReference.label",
                    prominence: "PRIMARY",
                    value: { kind: "REFERENCE", value: "CC-260Q-KJ0K-1CGJ-Q3NP-85" },
                  },
                ],
              },
            },
          ],
        },
        links: [
          {
            rel: "OPEN_CASE",
            labelId: "field.caseReference.label",
            link: {
              endpoint: "case.get",
              input: { target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" } },
            },
          },
        ],
      }),
    ),
  );
  navigate("/cases?q=different", true);
  const select = vi.fn();
  render(<CaseList onSelect={select} />);
  fireEvent.click(await screen.findByRole("link", { name: "CC-260Q-KJ0K-1CGJ-Q3NP-85" }));
  expect(select).toHaveBeenCalledWith(
    {
      kind: "ID",
      caseId: "20000000-0000-4000-8000-000000000001",
    },
    "case-exact-match",
  );
});
it("does not infer an exact match from a matching visible row when the core supplies null", async () => {
  vi.stubGlobal("fetch", vi.fn<typeof globalThis.fetch>().mockResolvedValue(page(false, null)));
  navigate("/cases?q=advised-case", true);
  render(<CaseList onSelect={vi.fn()} />);
  await screen.findByRole("link", { name: "CC-260Q-KJ0K-1CGJ-Q3NP-85" });
  expect(screen.queryByRole("button", { name: "Open CC-260Q-KJ0K-1CGJ-Q3NP-85" })).toBeNull();
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
                    joins: null,
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
                    values: [{ kind: "REFERENCE", value: "CC-SHSE-18HF-PX9X-RCYC-68" }],
                  },
                ],
              },
            },
          ],
        },
      }),
    ),
  );
  navigate("/cases?q=CC-%C5%BFHSE-18HF-PX9X-RCYC-68", true);
  render(<CaseList onSelect={vi.fn()} />);
  await screen.findByRole("link", { name: "CC-SHSE-18HF-PX9X-RCYC-68" });
  expect(screen.queryByRole("button", { name: "Open CC-SHSE-18HF-PX9X-RCYC-68" })).toBeNull();
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
              emptyId: "label.noCasesForView",
              content: {
                kind: "TABLE",
                columns: [
                  {
                    key: "caseReference",
                    labelId: "field.caseReference.label",
                    prominence: "PRIMARY",
                    joins: null,
                    isMoney: false,
                  },
                ],
                rows: [],
              },
            },
          ],
        },
        links: [],
      }),
    ),
  );
  navigate(`/cases?view=${view}`, true);
  render(<CaseList onSelect={vi.fn()} />);
  await screen.findByText("No cases match this view.");
  expect(screen.getAllByText("No cases match this view.")).toHaveLength(1);
  expect(screen.getByRole("tab", { name: `${label} 0` })).toHaveAttribute("aria-selected", "true");
  expect(screen.queryByText("No cases to show.")).toBeNull();
  expect(screen.queryByRole("button", { name: "Clear filters" })).toBeNull();
});
it("keeps existing rows visible during a quiet refresh", async () => {
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
  await screen.findByRole("link", { name: "CC-260Q-KJ0K-1CGJ-Q3NP-85" });
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(fetch.mock.calls[0]?.[0]).toBe("/api/case.list");
  fireEvent.click(
    screen.getByRole("button", {
      name: "Refresh",
    }),
  );
  expect(screen.getByRole("link", { name: "CC-260Q-KJ0K-1CGJ-Q3NP-85" })).toBeVisible();
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
    name: "CC-260Q-KJ0K-1CGJ-Q3NP-85",
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
        name: "Load more",
      }),
    );
    await screen.findByRole("alert");
    expect(
      screen.queryByRole("link", {
        name: "CC-260Q-KJ0K-1CGJ-Q3NP-85",
      }),
    ).toBeNull();
    expect(
      screen.queryByRole("button", {
        name: "Open a case",
      }),
    ).toBeNull();
    expect(
      screen.queryByRole("button", {
        name: "Load more",
      }),
    ).toBeNull();
  },
);
it("masks the last accepted rows after a request-local failure", async () => {
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
      name: "Load more",
    }),
  );
  await waitFor(() => {
    expect(screen.getByRole("alert")).toBeVisible();
  });
  expect(
    screen.queryByRole("link", {
      name: "CC-260Q-KJ0K-1CGJ-Q3NP-85",
    }),
  ).toBeNull();
});
