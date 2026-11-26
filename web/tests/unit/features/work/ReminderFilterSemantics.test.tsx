import { expect, it } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { mockEndpoint } from "../../../support/endpoint-mocks";
import {
  pendingOpen,
  completedOpen,
  cancelledOpen,
  completedClosed,
  completedLaterOpen,
  selectionPage,
} from "../../../support/reminder-selection.fixtures";
import { navigate } from "../../../../src/routing/navigation";
import { ReminderQueue } from "../../../../src/features/work/ReminderQueue";

it("shows the owner Pending/active default in manual mode while leaving ordinary missing request fields missing", async () => {
  const reads = mockEndpoint("reminder.list").mockResolvedValue(selectionPage([pendingOpen]));
  navigate("/reminders?filters=manual", true);
  render(<ReminderQueue />);
  await waitFor(() => {
    expect(reads).toHaveBeenCalledOnce();
  });
  expect(reads.mock.calls[0]?.[1]).toEqual({ limit: 50 });
  expect(screen.getByRole("combobox", { name: "Reminder state" })).toHaveTextContent("Pending");
  expect(screen.getByText("Pending open reminder")).toBeVisible();
  expect(screen.queryByText("Completed open reminder")).toBeNull();
  await userEvent.click(
    screen.getByRole("button", { name: /^(?:More filters|Filters \(\d+\))$/u }),
  );
  expect(screen.getByRole("checkbox", { name: "Include inactive case work" })).not.toBeChecked();
});
it("normal Any selection sends the explicit owner token and renders all three returned states without merging queries", async () => {
  const reads = mockEndpoint("reminder.list")
    .mockResolvedValueOnce(selectionPage([completedOpen]))
    .mockResolvedValueOnce(selectionPage([pendingOpen, completedOpen, cancelledOpen]));
  navigate("/reminders?filters=manual&state=COMPLETED&includeInactive=true", true);
  render(<ReminderQueue />);
  expect(await screen.findByText("Completed open reminder")).toBeVisible();
  await userEvent.click(screen.getByRole("combobox", { name: "Reminder state" }));
  await userEvent.click(screen.getByRole("option", { name: "Any" }));
  await waitFor(() => {
    expect(reads).toHaveBeenCalledTimes(2);
  });
  expect(reads.mock.calls[1]?.[1]).toEqual({ state: "ANY", includeInactive: true, limit: 50 });
  expect(new URLSearchParams(location.search).get("state")).toBe("ANY");
  for (const caption of [
    "Pending open reminder",
    "Completed open reminder",
    "Cancelled open reminder",
  ]) {
    expect(await screen.findByText(caption)).toBeVisible();
  }
  expect(screen.getByRole("combobox", { name: "Reminder state" })).toHaveTextContent("Any");
});
it("shows the owner view defaults and preserves an explicit false contrast against a closed completed row", async () => {
  const reads = mockEndpoint("reminder.list")
    .mockResolvedValueOnce(selectionPage([completedOpen, completedClosed]))
    .mockResolvedValueOnce(selectionPage([completedOpen]));
  navigate("/reminders?view=COMPLETED", true);
  render(<ReminderQueue />);
  expect(await screen.findByText("Completed closed reminder")).toBeVisible();
  expect(reads.mock.calls[0]?.[1]).toEqual({ view: "COMPLETED", limit: 50 });
  expect(screen.getByRole("combobox", { name: "Reminder state" })).toHaveTextContent("Any");
  await userEvent.click(
    screen.getByRole("button", { name: /^(?:More filters|Filters \(\d+\))$/u }),
  );
  const inactive = screen.getByRole("checkbox", { name: "Include inactive case work" });
  expect(inactive).toBeEnabled();
  expect(inactive).toBeChecked();
  await userEvent.click(inactive);
  await userEvent.click(screen.getByRole("button", { name: "Apply filters" }));
  await waitFor(() => {
    expect(reads.mock.calls[1]?.[1]).toEqual({
      view: "COMPLETED",
      includeInactive: false,
      limit: 50,
    });
  });
  await waitFor(() => {
    expect(screen.queryByText("Completed closed reminder")).toBeNull();
  });
  expect(screen.getByText("Completed open reminder")).toBeVisible();
  expect(new URLSearchParams(location.search).get("includeInactive")).toBe("false");
  await userEvent.click(
    screen.getByRole("button", { name: /^(?:More filters|Filters \(\d+\))$/u }),
  );
  expect(screen.getByRole("checkbox", { name: "Include inactive case work" })).not.toBeChecked();
});
it("keeps false and Any through a supplied-target page continuation, clear restriction and reload", async () => {
  const reads = mockEndpoint("reminder.list")
    .mockResolvedValueOnce(selectionPage([completedOpen], "old-scope-cursor"))
    .mockResolvedValueOnce(selectionPage([completedLaterOpen]))
    .mockResolvedValue(selectionPage([completedOpen]));
  navigate(
    "/reminders?view=COMPLETED&state=ANY&includeInactive=false&caseReference=VISIBLE-CASE",
    true,
  );
  const view = render(<ReminderQueue />);
  expect(await screen.findByText("Completed open reminder")).toBeVisible();
  await userEvent.click(screen.getByRole("button", { name: "Load more" }));
  await waitFor(() => {
    expect(reads.mock.calls[1]?.[1]).toMatchObject({
      cursor: "old-scope-cursor",
      target: { kind: "REFERENCE", caseReference: "VISIBLE-CASE" },
      state: "ANY",
      includeInactive: false,
    });
  });
  await userEvent.click(screen.getByRole("button", { name: "Clear case restriction" }));
  await waitFor(() => {
    expect(reads.mock.calls.at(-1)?.[1]).toEqual({
      view: "COMPLETED",
      state: "ANY",
      includeInactive: false,
      limit: 50,
    });
  });
  expect(location.search).not.toMatch(/caseReference|cursor/u);
  await userEvent.click(
    screen.getByRole("button", { name: /^(?:More filters|Filters \(\d+\))$/u }),
  );
  expect(screen.getByRole("checkbox", { name: "Include inactive case work" })).not.toBeChecked();
  view.unmount();
  render(<ReminderQueue />);
  await waitFor(() => {
    expect(reads.mock.calls.at(-1)?.[1]).toEqual({
      view: "COMPLETED",
      state: "ANY",
      includeInactive: false,
      limit: 50,
    });
  });
});
