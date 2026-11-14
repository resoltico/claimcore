import { fields } from "../support/service-ui.fixtures";
import { humanReadReply } from "../support/human-read-reply.fixtures";
import { timelineDocument } from "../support/timeline-document.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { workBasis } from "../support/inputs";
import { current } from "../support/presentation-state.fixtures";
import { Dashboard } from "../../src/views/Dashboard";
import { navigate } from "../../src/routing/navigation";
const descriptors = {
  scrollY: Object.getOwnPropertyDescriptor(window, "scrollY"),
  scrollTo: Object.getOwnPropertyDescriptor(window, "scrollTo"),
};
afterEach(() => {
  for (const [key, descriptor] of Object.entries(descriptors)) {
    if (descriptor !== undefined) {
      Object.defineProperty(window, key, descriptor);
    }
  }
});
it.each([
  { retained: true, interrupt: false },
  { retained: false, interrupt: false },
  { retained: true, interrupt: true },
])(
  "returns to the exact work query with row availability=$retained and user override=$interrupt and leaves history state empty",
  async ({ retained, interrupt }) => {
    stubDashboardContext();
    const page = (includeRow: boolean) =>
      ({
        kind: "outcome",
        status: 200,
        value: humanReadReply("case.list", {
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
                  rows: includeRow
                    ? [
                        {
                          key: current.subject.caseId,
                          link: {
                            endpoint: "case.get",
                            input: { target: { kind: "ID", caseId: current.subject.caseId } },
                          },
                          values: [{ kind: "REFERENCE", value: fields.caseReference }],
                        },
                      ]
                    : [],
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
        }),
      }) as const;
    const pending = Promise.withResolvers<ReturnType<typeof page>>();
    const listing = mockEndpoint("case.list").mockResolvedValueOnce(page(true));
    if (interrupt) {
      listing.mockReturnValue(pending.promise);
    } else {
      listing.mockResolvedValue(page(retained));
    }
    mockEndpoint("case.summary").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        ...humanReadReply("case.summary", {
          screen: { regions: [] },
          subject: { kind: "LIST" },
          actions: [],
          links: [],
          history: null,
          nextCursor: null,
          basis: { kind: "SNAPSHOT", observation: workBasis },
          disclosure: "CURRENT",
        }),
      },
    });
    mockEndpoint("case.get").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        outcome: { kind: "SUCCEEDED", document: current },
      },
    });
    mockEndpoint("case.timeline").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        ...humanReadReply("case.timeline", timelineDocument()),
      },
    });
    const scroll = vi.fn();
    Object.defineProperty(window, "scrollY", { configurable: true, value: 340 });
    Object.defineProperty(window, "scrollTo", { configurable: true, value: scroll });
    navigate("/cases?view=ALL&q=CASE&order=RECENT", true);
    const user = userEvent.setup();
    render(<Dashboard />);
    await user.click(await screen.findByRole("link", { name: fields.caseReference }));
    await screen.findByRole("heading", {
      name: (_name, element) => element.id === "case-detail-title",
    });
    await user.click(screen.getByRole("button", { name: "Back to cases" }));
    await waitFor(() => {
      expect(location.pathname + location.search).toBe("/cases?view=ALL&q=CASE&order=RECENT");
    });
    if (interrupt) {
      const summary = document.querySelector<HTMLElement>(".exact-lookup > summary")!;
      await user.pointer({ target: summary, keys: "[MouseLeft>]" });
      await act(async () => {
        pending.resolve(page(true));
        await pending.promise;
      });
      await user.pointer({ target: summary, keys: "[/MouseLeft]" });
      await waitFor(() => {
        expect(document.querySelector(".exact-lookup")).toHaveAttribute("open", "");
      });
      expect(scroll).not.toHaveBeenCalled();
    } else if (retained) {
      await waitFor(() => {
        expect(screen.getByRole("link", { name: fields.caseReference })).toHaveFocus();
      });
      expect(scroll).toHaveBeenLastCalledWith(0, 340);
    } else {
      await waitFor(() => {
        expect(screen.getByRole("heading", { name: "Cases", level: 1 })).toHaveFocus();
      });
      expect(scroll).not.toHaveBeenCalled();
    }
    expect(history.state).toBeNull();
  },
);
