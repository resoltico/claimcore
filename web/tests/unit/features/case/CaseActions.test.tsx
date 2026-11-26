import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { operationAffordance, withRecommendation } from "../../../support/affordances.fixtures";
import { current } from "../../../support/operation-form.fixtures";
import { CaseActions } from "../../../../src/features/case/CaseActions";
const primary = operationAffordance(
  "case.amendRegistration",
  { kind: "CASE", target: { kind: "REFERENCE", caseReference: "CASE-1" }, expectedRevision: 4 },
  {},
  [],
  {},
);
const alternative = operationAffordance("case.decide", primary.binding);
it("renders the supplied recommendation and passes its control", async () => {
  const onAct = vi.fn();
  const view = render(
    <CaseActions
      current={withRecommendation({ ...current, actions: [alternative, primary] }, primary.task)}
      onAct={onAct}
    />,
  );
  expect(view.container.querySelector(".ui-button--primary")).toHaveTextContent(
    "Update claim details",
  );
  await userEvent.setup().click(screen.getByRole("button", { name: "Update claim details" }));
  expect(onAct).toHaveBeenCalledExactlyOnceWith(primary, expect.any(String));
});
it("does not invent a primary", async () => {
  const view = render(
    <CaseActions current={{ ...current, actions: [alternative] }} onAct={vi.fn()} />,
  );
  expect(view.container.querySelector(".ui-button--primary")).toBeNull();
  await userEvent.setup().click(screen.getByRole("button", { name: "More actions" }));
  expect(screen.getByRole("menuitem", { name: "Record payment decision" })).toBeVisible();
});
it("withdraws controls when Core withdraws the offer", () => {
  const onAct = vi.fn();
  const view = render(
    <CaseActions
      current={withRecommendation({ ...current, actions: [primary] }, primary.task)}
      onAct={onAct}
    />,
  );
  view.rerender(<CaseActions current={{ ...current, actions: [] }} onAct={onAct} />);
  expect(screen.queryByRole("button")).toBeNull();
  expect(onAct).not.toHaveBeenCalled();
});
it("disables an offered action with Core refusal advice", async () => {
  render(
    <CaseActions
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
  await userEvent.setup().click(screen.getByRole("button", { name: "More actions" }));
  expect(screen.getByRole("menuitem", { name: "Record payment decision" })).toHaveAttribute(
    "aria-disabled",
    "true",
  );
});

it("keeps a supplied nonrecommended action reachable through the overflow control", async () => {
  const onAct = vi.fn();
  render(
    <CaseActions
      current={{ ...current, screen: { regions: [] }, actions: [alternative] }}
      onAct={onAct}
    />,
  );
  await userEvent.setup().click(screen.getByRole("button", { name: "More actions" }));
  const action = screen.getByRole("menuitem", { name: "Record payment decision" });
  expect(action).toBeVisible();
  await userEvent.setup().click(action);
  expect(onAct).toHaveBeenCalledExactlyOnceWith(alternative, expect.any(String));
});
