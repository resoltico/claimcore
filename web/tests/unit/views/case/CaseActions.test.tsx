import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { operationAffordance } from "../../../support/affordances.fixtures";
import { current } from "../../../support/operation-form.fixtures";
import { CaseActions, CaseActionAlternatives } from "../../../../src/views/case/CaseActions";
const primary = operationAffordance(
  "case.amendRegistration",
  { kind: "CASE", target: { kind: "REFERENCE", caseReference: "CASE-1" }, expectedRevision: 4 },
  {},
  [],
  {},
  true,
);
const alternative = operationAffordance("case.decide", primary.binding);
it("renders the exact supplied primary and invokes its task", async () => {
  const onAct = vi.fn();
  const view = render(
    <CaseActions current={{ ...current, actions: [alternative, primary] }} onAct={onAct} />,
  );
  expect(view.container.querySelector("button.primary")).toHaveAttribute(
    "data-act",
    "case.amendRegistration",
  );
  await userEvent.setup().click(screen.getByRole("button", { name: /^Update claim details:/u }));
  expect(onAct).toHaveBeenCalledExactlyOnceWith("case.amendRegistration");
});
it("does not invent a primary", () => {
  const view = render(
    <CaseActionAlternatives current={{ ...current, actions: [alternative] }} onAct={vi.fn()} />,
  );
  expect(view.container.querySelector("button.primary")).toBeNull();
  expect(screen.getByRole("button", { name: /^Record payment decision:/u })).toBeVisible();
});
it("withdraws controls when Core withdraws the offer", () => {
  const onAct = vi.fn();
  const view = render(<CaseActions current={{ ...current, actions: [primary] }} onAct={onAct} />);
  view.rerender(<CaseActions current={{ ...current, actions: [] }} onAct={onAct} />);
  expect(screen.queryByRole("button")).toBeNull();
  expect(onAct).not.toHaveBeenCalled();
});
it("disables an offered action with Core refusal advice", () => {
  render(
    <CaseActionAlternatives
      current={{
        ...current,
        actions: [
          {
            ...alternative,
            blocked: {
              message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
              action: "ASK_FOR_ACCESS",
            },
          },
        ],
      }}
      onAct={vi.fn()}
    />,
  );
  expect(screen.getByRole("button", { name: /^Record payment decision:/u })).toBeDisabled();
});
