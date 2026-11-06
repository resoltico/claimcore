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
    state: { kind: "ACTIVE" },
  });
  render(
    <table>
      <tbody>
        <CaseRow value={value} onSelect={select} />
      </tbody>
    </table>,
  );
  expect(screen.getByRole("row")).toHaveAccessibleName(
    `${reference} · Synthetic claimant · Synthetic insurer`,
  );
  expect(screen.getByTitle("Synthetic claimant")).toBeVisible();
  expect(screen.getByTitle("Synthetic insurer")).toBeVisible();
  const link = screen.getByRole("link", { name: reference });
  expect(screen.getAllByRole("link")).toHaveLength(1);
  expect(screen.queryByRole("button")).toBeNull();
  expect(link).toHaveAttribute("title", reference);
  expect(link.textContent).toBe(reference);
  expect(link.querySelector(".reference-suffix")).toHaveTextContent("DING-1234");
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
    state: { kind: "VOIDED" },
    headline: "PAID",
    attention: ["UNASSIGNED", "OVERDUE"],
  });
  const view = render(
    <table>
      <tbody>
        <CaseRow value={value} onSelect={vi.fn()} />
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
        <CaseRow value={value} onSelect={vi.fn()} />
      </tbody>
    </table>,
  );
  expect(screen.getByText("Core-supplied next action")).toBeVisible();
  expect(screen.getByText("Assigned profile name")).toBeVisible();
  const due = view.container.querySelector(".worklist-next-action time")!;
  expect(due).toHaveAttribute("datetime", "2026-11-05");
  expect(due).toHaveAttribute("data-due", "UPCOMING");
  expect(due).toHaveAttribute("title", "Upcoming");
  expect(due).toHaveAccessibleName("05/11/2026 · Upcoming");
  expect(view.container.querySelector(".worklist-responsible")).toHaveTextContent(
    "Unavailable for new assignment",
  );
});

it("shows one timing badge while preserving every Core attention flag and the reminder timing", () => {
  const view = render(
    <table>
      <tbody>
        <CaseRow
          value={caseSummaryInput({
            attention: ["OVERDUE", "UNASSIGNED"],
            nextAction: {
              reminderId: "10000000-0000-4000-8000-000000000001",
              action: "Call claimant",
              due: "2026-11-04",
              dueState: "OVERDUE",
            },
          })}
          onSelect={vi.fn()}
        />
      </tbody>
    </table>,
  );
  expect(screen.getAllByText("Overdue", { exact: true })).toHaveLength(1);
  expect(
    [...view.container.querySelectorAll(".case-attention [data-attention]")].map((el) =>
      el.getAttribute("data-attention"),
    ),
  ).toEqual(["OVERDUE", "UNASSIGNED"]);
  expect(view.container.querySelector(".worklist-next-action time")).toHaveAttribute(
    "title",
    "Overdue",
  );
});
