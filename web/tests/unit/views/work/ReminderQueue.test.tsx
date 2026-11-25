import { humanReadReply } from "../../../support/human-read-reply.fixtures";
import { within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { mockEndpoint } from "../../../support/endpoint-mocks";
import { workBasis } from "../../../support/inputs";
import { ReminderQueue } from "../../../../src/views/work/ReminderQueue";
import { navigate } from "../../../../src/routing/navigation";
it("selects the first core view and changes views without adding hidden state predicates", async () => {
  const reads = mockEndpoint("reminder.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadReply("reminder.list", {
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
  navigate("/reminders?q=literal", true);
  render(<ReminderQueue />);
  await waitFor(() => {
    expect(reads).toHaveBeenCalledOnce();
  });
  expect(reads.mock.calls[0]?.[1]).toEqual({ view: "PENDING", text: "literal", limit: 50 });
  const user = userEvent.setup();
  await user.type(screen.getByRole("searchbox", { name: "Filter this view" }), " raw draft");
  await user.click(screen.getByRole("tab", { name: "Completed 0" }));
  await waitFor(() => {
    expect(reads).toHaveBeenCalledTimes(2);
  });
  expect(reads.mock.calls[1]?.[1]).toEqual({ view: "COMPLETED", text: "literal", limit: 50 });
  expect(screen.getByRole("searchbox", { name: "Filter this view" })).toHaveValue(
    "literal raw draft",
  );
  expect(location.pathname + location.search).toBe("/reminders?view=COMPLETED&q=literal");
});

it("changes only the selected view when switching mode and shows owner defaults without sending interface mode", async () => {
  const reads = mockEndpoint("reminder.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadReply("reminder.list", {
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
  navigate("/reminders?view=PENDING&q=literal", true);
  render(<ReminderQueue />);
  await waitFor(() => {
    expect(reads).toHaveBeenCalledOnce();
  });
  const user = userEvent.setup();
  const moreFilters = () =>
    screen.getByRole("button", { name: /^(?:More filters|Filters \(\d+\))$/u });
  await user.click(moreFilters());
  const inactive = () => screen.getByRole("checkbox", { name: "Include inactive case work" });
  expect(inactive()).toBeEnabled();
  expect(inactive()).toBeChecked();
  const mode = () =>
    within(screen.getByRole("dialog")).getByRole("button", { name: "Manual filters" });
  expect(mode()).toHaveAttribute("aria-pressed", "false");
  await user.click(mode());
  await waitFor(() => {
    expect(reads).toHaveBeenCalledTimes(2);
  });
  expect(reads.mock.calls[1]?.[1]).toEqual({ text: "literal", limit: 50 });
  expect(location.pathname + location.search).toBe("/reminders?q=literal&filters=manual");
  expect(screen.queryByRole("tab")).toBeNull();
  await user.click(moreFilters());
  expect(inactive()).toBeEnabled();
  expect(inactive()).not.toBeChecked();
  expect(mode()).toHaveAttribute("aria-pressed", "true");
  await user.click(inactive());
  await user.click(screen.getByRole("button", { name: "Apply filters" }));
  await waitFor(() => {
    expect(reads).toHaveBeenCalledTimes(3);
  });
  expect(reads.mock.calls[2]?.[1]).toEqual({ text: "literal", includeInactive: true, limit: 50 });
  await user.click(moreFilters());
  await user.click(mode());
  await waitFor(() => {
    expect(reads).toHaveBeenCalledTimes(4);
  });
  expect(reads.mock.calls[3]?.[1]).toEqual({
    view: "PENDING",
    text: "literal",
    includeInactive: true,
    limit: 50,
  });
  await user.click(moreFilters());
  expect(inactive()).toBeEnabled();
  expect(inactive()).toBeChecked();
  expect(mode()).toHaveAttribute("aria-pressed", "false");
  await user.keyboard("{Escape}");
  expect(screen.getByRole("tab", { name: "Pending 0" })).toHaveAttribute("aria-selected", "true");
});
