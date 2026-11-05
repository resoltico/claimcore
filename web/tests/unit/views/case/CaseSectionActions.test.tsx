import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen } from "../../../support/presentation-test-support";
import { caseViewInput } from "../../../support/inputs";
import { caseAdvice } from "../../../support/case-advice.fixtures";
import { fields } from "../../../support/service-ui.fixtures";
import { CaseSectionActions } from "../../../../src/views/case/CaseSectionActions";

const current = caseViewInput({
  caseReference: "SECTION-1",
  revision: 2,
  state: { kind: "ACTIVE", fields },
  advice: { ...caseAdvice(["CORRECT", "RECORD_PAYMENT", "CLOSE", "ERASE"]), history: [] },
});
it("offers only the Core-advised fact acts affecting this section and restores the visible trigger", async () => {
  const user = userEvent.setup();
  const onAct = vi.fn();
  const view = render(
    <CaseSectionActions current={current} section="registration" onAct={onAct} />,
  );
  const trigger = screen.getByRole("button", { name: "Correct case facts", exact: true });
  expect(screen.queryByRole("button", { name: "Record actual payment" })).toBeNull();
  expect(screen.queryByRole("button", { name: "Erase case" })).toBeNull();
  await user.click(trigger);
  expect(onAct).toHaveBeenCalledExactlyOnceWith("CORRECT");
  expect(view.container.querySelector("details")).toBeNull();
  expect(trigger).toHaveFocus();
});
it("keeps both offered payment choices reachable without inventing a primary", async () => {
  const user = userEvent.setup();
  const view = render(<CaseSectionActions current={current} section="payment" onAct={vi.fn()} />);
  const summary = view.container.querySelector("summary")!;
  await user.click(summary);
  expect(screen.getByRole("button", { name: "Correct case facts", exact: true })).toBeVisible();
  expect(screen.getByRole("button", { name: "Record actual payment", exact: true })).toBeVisible();
  expect(view.container.querySelector(".primary")).toBeNull();
  await user.keyboard("{Escape}");
  expect(view.container.querySelector("details")).not.toHaveAttribute("open");
  expect(summary).toHaveFocus();
});
it("offers no section edits during maintenance or without a relevant offer", () => {
  const view = render(
    <CaseSectionActions
      current={{
        ...current,
        advice: { ...current.advice, maintenance: { active: true, generation: 1 } },
      }}
      section="payment"
      onAct={vi.fn()}
    />,
  );
  expect(view.container.innerHTML).toBe("");
  view.rerender(
    <CaseSectionActions
      current={{ ...current, advice: { ...current.advice, acts: ["CLOSE", "ERASE"] } }}
      section="registration"
      onAct={vi.fn()}
    />,
  );
  expect(view.container.innerHTML).toBe("");
});
