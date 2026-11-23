import { expect, it } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor, fireEvent } from "../support/presentation-test-support";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { humanReadReply } from "../support/human-read-reply.fixtures";
import { workBasis } from "../support/inputs";
import { chooseOption } from "../support/choice-entry";
import { navigate } from "../../src/routing/navigation";
import { Dashboard } from "../../src/views/Dashboard";
const target = { kind: "ID" as const, caseId: "20000000-0000-4000-8000-000000000001" };
const page = (reference: string | null, nextCursor: string | null) => ({
  kind: "outcome" as const,
  status: 200,
  value: humanReadReply("reminder.list", {
    screen: {
      regions:
        reference === null
          ? []
          : [
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
                  rows: [{ key: reference, values: [{ kind: "REFERENCE", value: reference }] }],
                },
              },
            ],
    },
    subject: { kind: "LIST" },
    actions: [],
    links: [],
    history: null,
    nextCursor,
    basis: { kind: "SNAPSHOT", observation: workBasis },
    disclosure: "CURRENT",
  }),
});
it("composes target assignment due text and state through queue paging and resets the cursor on filter changes", async () => {
  stubDashboardContext();
  const list = mockEndpoint("reminder.list")
    .mockResolvedValueOnce(page("FIRST-PAGE", "next-selected-page"))
    .mockResolvedValueOnce(page("SECOND-PAGE", null))
    .mockResolvedValue(page(null, null));
  navigate(
    `/reminders?state=PENDING&due=TODAY&assignment=MINE&q=A%26B&caseId=${target.caseId}&filters=manual`,
    true,
  );
  const user = userEvent.setup();
  render(<Dashboard />);
  await screen.findByText("FIRST-PAGE");
  const expected = {
    target,
    state: "PENDING",
    due: "TODAY",
    assignment: "MINE",
    text: "A&B",
    limit: 50,
  };
  expect(list.mock.calls[0]![1]).toEqual(expected);
  await user.click(screen.getByRole("button", { name: "Load more" }));
  await screen.findByText("SECOND-PAGE");
  expect(list.mock.calls[1]![1]).toEqual({ ...expected, cursor: "next-selected-page" });
  await chooseOption(user, screen.getByRole("combobox", { name: "Reminder due" }), "UPCOMING");
  await waitFor(() => {
    expect(list).toHaveBeenCalledTimes(3);
  });
  expect(list.mock.calls[2]![1]).toEqual({ ...expected, due: "UPCOMING" });
  await waitFor(() => {
    expect(screen.queryByText("FIRST-PAGE")).toBeNull();
  });
  expect(screen.queryByText("SECOND-PAGE")).toBeNull();
  await chooseOption(user, screen.getByRole("combobox", { name: "Reminder state" }), "COMPLETED");
  await chooseOption(user, screen.getByRole("combobox", { name: "Responsibility" }), "UNASSIGNED");
  const search = screen.getByRole("searchbox", { name: "Filter this view" });
  fireEvent.change(search, { target: { value: "New & exact" } });
  await user.click(search);
  await user.keyboard("{Enter}");
  await waitFor(() => {
    expect(list.mock.calls.at(-1)![1]).toEqual({
      ...expected,
      due: "UPCOMING",
      state: "COMPLETED",
      assignment: "UNASSIGNED",
      text: "New & exact",
    });
  });
  const url = new URL(location.href);
  expect(url.pathname).toBe("/reminders");
  expect(Object.fromEntries(url.searchParams)).toEqual({
    state: "COMPLETED",
    due: "UPCOMING",
    assignment: "UNASSIGNED",
    q: "New & exact",
    caseId: target.caseId,
    filters: "manual",
  });
  expect(list.mock.calls.slice(2).every(([, input]) => !("cursor" in input))).toBe(true);
});
