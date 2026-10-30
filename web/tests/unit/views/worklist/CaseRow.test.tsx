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
  expect(select).toHaveBeenCalledWith(reference);
  select.mockClear();
  fireEvent.click(link, { ctrlKey: true });
  expect(select).not.toHaveBeenCalled();
  fireEvent.click(link, { metaKey: true });
  expect(select).not.toHaveBeenCalled();
  fireEvent.click(link, { button: 1 });
  expect(select).not.toHaveBeenCalled();
});
it("prioritizes voided state over status and retains decided progress as secondary text", () => {
  const value = caseSummaryInput({
    state: { kind: "VOIDED", status: "CLOSED", progress: "DECIDED" },
  });
  const view = render(
    <table>
      <tbody>
        <CaseRow value={value} businessDate="2026-09-09" onSelect={vi.fn()} />
      </tbody>
    </table>,
  );
  expect(view.container.querySelectorAll(".badge")).toHaveLength(1);
  expect(view.container.querySelector(".badge")).toHaveTextContent("Voided");
  expect(screen.getByText("was Decided")).toBeVisible();
});
