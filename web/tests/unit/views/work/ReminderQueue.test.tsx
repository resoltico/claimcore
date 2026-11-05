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
      outcome: { kind: "SUCCEEDED", data: { basis: workBasis, items: [], nextCursor: null } },
    },
  });
  navigate("/reminders?q=literal", true);
  render(<ReminderQueue />);
  await waitFor(() => {
    expect(reads).toHaveBeenCalledOnce();
  });
  expect(reads.mock.calls[0]?.[1]).toEqual({ view: "PENDING", text: "literal", limit: 50 });
  const user = userEvent.setup();
  await user.type(screen.getByRole("searchbox", { name: "Search" }), " raw draft");
  await user.click(screen.getByRole("tab", { name: "Completed 0" }));
  await waitFor(() => {
    expect(reads).toHaveBeenCalledTimes(2);
  });
  expect(reads.mock.calls[1]?.[1]).toEqual({ view: "COMPLETED", text: "literal", limit: 50 });
  expect(screen.getByRole("searchbox", { name: "Search" })).toHaveValue("literal raw draft");
  expect(location.pathname + location.search).toBe("/reminders?view=COMPLETED&q=literal");
});

it("requires explicit manual filters for inactive work and never sends interface mode to Core", async () => {
  const reads = mockEndpoint("reminder.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: { kind: "SUCCEEDED", data: { basis: workBasis, items: [], nextCursor: null } },
    },
  });
  navigate("/reminders?view=PENDING&q=literal", true);
  render(<ReminderQueue />);
  await waitFor(() => {
    expect(reads).toHaveBeenCalledOnce();
  });
  const inactive = () => screen.getByRole("checkbox", { name: "Include inactive case work" });
  expect(inactive()).toBeDisabled();
  expect(inactive()).toHaveAccessibleDescription("Use manual filters to include inactive work.");
  const mode = screen.getByRole("button", { name: "Manual filters" });
  expect(mode).toHaveAttribute("aria-pressed", "false");
  const user = userEvent.setup();
  await user.click(mode);
  await waitFor(() => {
    expect(reads).toHaveBeenCalledTimes(2);
  });
  expect(reads.mock.calls[1]?.[1]).toEqual({ text: "literal", limit: 50 });
  expect(location.pathname + location.search).toBe("/reminders?q=literal&filters=manual");
  expect(screen.queryByRole("tab")).toBeNull();
  expect(inactive()).not.toBeDisabled();
  expect(mode).toHaveAttribute("aria-pressed", "true");
  await user.click(inactive());
  await waitFor(() => {
    expect(reads).toHaveBeenCalledTimes(3);
  });
  expect(reads.mock.calls[2]?.[1]).toEqual({ text: "literal", includeInactive: true, limit: 50 });
  await user.click(mode);
  await waitFor(() => {
    expect(reads).toHaveBeenCalledTimes(4);
  });
  expect(reads.mock.calls[3]?.[1]).toEqual({
    view: "PENDING",
    text: "literal",
    includeInactive: true,
    limit: 50,
  });
  expect(inactive()).toBeDisabled();
  expect(inactive()).toBeChecked();
  expect(mode).toHaveAttribute("aria-pressed", "false");
  expect(screen.getByRole("tab", { name: "Pending 0" })).toHaveAttribute("aria-selected", "true");
});
