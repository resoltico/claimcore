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
