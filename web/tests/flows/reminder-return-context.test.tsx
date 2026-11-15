import { expect, it, vi } from "vitest";
import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { humanReadReply } from "../support/human-read-reply.fixtures";
import { caseDocumentInput } from "../support/case-document.fixtures";
import { workBasis } from "../support/inputs";
import { Dashboard } from "../../src/views/Dashboard";
import { navigate, routeSnapshot, setNavigationBlocker } from "../../src/routing/navigation";

it("waits for the initial reminder page before restoring filtered queue focus and scroll", async () => {
  stubDashboardContext();
  const target = { kind: "ID" as const, caseId: "20000000-0000-4000-8000-000000000001" };
  const reminderId = "20000000-0000-4000-8000-000000000099";
  const empty = {
    subject: { kind: "LIST" as const },
    actions: [],
    links: [],
    history: null,
    nextCursor: null,
    disclosure: "CURRENT" as const,
    basis: { kind: "SNAPSHOT" as const, observation: workBasis },
  };
  const listing = {
    kind: "outcome" as const,
    status: 200,
    value: humanReadReply("reminder.list", {
      ...empty,
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
                  key: reminderId,
                  link: { endpoint: "reminder.get", input: { target, reminderId } },
                  values: [{ kind: "REFERENCE", value: "QUEUE-FOCUS" }],
                },
              ],
            },
          },
        ],
      },
    }),
  };
  const pending = Promise.withResolvers<typeof listing>();
  const list = mockEndpoint("reminder.list")
    .mockResolvedValueOnce(listing)
    .mockReturnValueOnce(pending.promise);
  mockEndpoint("case.get").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("case.get", caseDocumentInput({ history: null })),
  });
  mockEndpoint("reminder.get").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("reminder.get", { ...empty, screen: { regions: [] } }),
  });
  const originalY = Object.getOwnPropertyDescriptor(window, "scrollY");
  const originalScroll = Object.getOwnPropertyDescriptor(window, "scrollTo");
  const scroll = vi.fn();
  Object.defineProperty(window, "scrollY", { configurable: true, value: 420 });
  Object.defineProperty(window, "scrollTo", { configurable: true, value: scroll });
  try {
    navigate("/reminders?view=PENDING&q=QUEUE", true);
    const user = userEvent.setup();
    render(<Dashboard />);
    await user.click(await screen.findByRole("link", { name: "QUEUE-FOCUS" }));
    await user.click(await screen.findByRole("button", { name: "Return to workspace" }));
    await waitFor(() => {
      expect(list).toHaveBeenCalledTimes(2);
    });
    expect(screen.queryByRole("link", { name: "QUEUE-FOCUS" })).toBeNull();
    await act(async () => {
      pending.resolve(listing);
      await pending.promise;
    });
    await waitFor(() => {
      expect(screen.getByRole("link", { name: "QUEUE-FOCUS" })).toHaveFocus();
    });
    expect(location.pathname + location.search).toBe("/reminders?view=PENDING&q=QUEUE");
    expect(scroll).toHaveBeenLastCalledWith(0, 420);
  } finally {
    if (originalY !== undefined) {
      Object.defineProperty(window, "scrollY", originalY);
    }
    if (originalScroll !== undefined) {
      Object.defineProperty(window, "scrollTo", originalScroll);
    }
  }
});

it("cancels an approved traversal synchronously when the verified workspace unmounts", async () => {
  stubDashboardContext();
  navigate("/settings", true);
  const view = render(<Dashboard />);
  await waitFor(() => {
    expect(document.querySelector('nav a[href="/cases"]')).not.toBeNull();
  });
  act(() => {
    navigate("/owner-first", true);
    navigate("/owner-middle");
    navigate("/owner-draft");
  });
  const continuation: { approve?: () => void } = {};
  const release = setNavigationBlocker((proceed) => {
    continuation.approve = proceed;
    return false;
  });
  history.go(-2);
  await waitFor(() => {
    expect(continuation.approve).toBeDefined();
    expect(location.pathname).toBe("/owner-draft");
  });
  act(() => {
    continuation.approve?.();
    view.unmount();
    release();
  });
  await act(async () => {
    await new Promise((resolve) => {
      setTimeout(resolve, 100);
    });
  });
  expect(location.pathname).toBe("/owner-draft");
  expect(routeSnapshot()).toBe("/owner-draft");
});
