import userEvent from "@testing-library/user-event";
import { fireEvent } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { render, screen } from "../../../support/presentation-test-support";
import { caseSummaryInput } from "../../../support/inputs";
import { CaseRow } from "../../../../src/views/worklist/CaseRow";
it("makes the whole row one native keyboard link with full reference and no copy control", async () => {
  const user = userEvent.setup();
  const select = vi.fn();
  const reference = "A-VERY-LONG-REFERENCE-ENDING-1234";
  const value = caseSummaryInput({
    caseReference: reference,
    state: { kind: "ACTIVE", status: "OPENED", incidentNotificationDate: "2026-09-02" },
  });
  render(
    <table>
      <tbody>
        <CaseRow value={value} businessDate="2026-09-09" onSelect={select} />
      </tbody>
    </table>,
  );
  const link = screen.getByRole("link", { name: reference });
  expect(screen.getAllByRole("link")).toHaveLength(1);
  expect(screen.queryByRole("button")).toBeNull();
  expect(link).toHaveAttribute("title", reference);
  expect(link.textContent).toBe(reference);
  expect(link.querySelector(".reference-suffix")).toHaveTextContent("DING-1234");
  expect(screen.getByText("7 days")).toBeVisible();
  await user.tab();
  expect(link).toHaveFocus();
  await user.keyboard("{Enter}");
  expect(select).toHaveBeenCalledWith({ kind: "ID", caseId: value.caseId });
  select.mockClear();
  fireEvent.click(link, { ctrlKey: true });
  expect(select).not.toHaveBeenCalled();
  fireEvent.click(link, { metaKey: true });
  expect(select).not.toHaveBeenCalled();
  fireEvent.click(link, { button: 1 });
  expect(select).not.toHaveBeenCalled();
});
it("renders core headline and ordered attention without deriving them from state", () => {
  const value = caseSummaryInput({
    state: { kind: "VOIDED", status: "CLOSED", progress: "DECIDED" },
    headline: "PAID",
    attention: ["UNASSIGNED", "OVERDUE"],
  });
  const view = render(
    <table>
      <tbody>
        <CaseRow value={value} businessDate="2026-09-09" onSelect={vi.fn()} />
      </tbody>
    </table>,
  );
  expect(view.container.querySelectorAll(".badge")).toHaveLength(3);
  expect(view.container.querySelector(".case-headline")).toHaveTextContent("Paid");
  expect(
    [...view.container.querySelectorAll(".case-attention li")].map((item) => item.textContent),
  ).toEqual(["Unassigned", "Overdue"]);
  expect(screen.queryByText("Voided")).toBeNull();
  expect(screen.queryByText("was Decided")).toBeNull();
});
it("renders the supplied scheduling projection rather than reconstructing work state", () => {
  const value = caseSummaryInput({
    nextAction: {
      reminderId: "10000000-0000-4000-8000-000000000001",
      action: "Core-supplied next action",
      due: "2026-11-05",
      dueState: "UPCOMING",
    },
    responsible: {
      actorId: "10000000-0000-4000-8000-000000000002",
      displayName: { kind: "NAMED", value: "Assigned profile name" },
      principalKind: "HUMAN",
      availability: "DISABLED",
    },
    pendingReminders: 3,
  });
  const view = render(
    <table>
      <tbody>
        <CaseRow value={value} businessDate="2026-11-04" onSelect={vi.fn()} />
      </tbody>
    </table>,
  );
  expect(screen.getByText("Core-supplied next action")).toBeVisible();
  expect(screen.getByText("Assigned profile name")).toBeVisible();
  expect(view.container.querySelector(".worklist-next-action")).toHaveTextContent("Upcoming");
  expect(view.container.querySelector(".worklist-responsible")).toHaveTextContent(
    "Unavailable for new assignment",
  );
});
