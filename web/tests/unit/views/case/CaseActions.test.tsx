import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { current } from "../../../support/operation-form.fixtures";
import { CaseActions, CaseActionAlternatives } from "../../../../src/views/case/CaseActions";
import type { RegionContent } from "@contracts/client";

const supplied = (content: Extract<RegionContent, { kind: "ACTIONS" }>) => ({
  ...current,
  screen: { regions: [{ role: "RECOMMENDATION" as const, content }] },
  context: {
    ...current.context,
    correction: null,
    tasks: ["CASE:DECIDE", "CASE:AMEND_REGISTRATION", "CASE:VOID", "CASE:ERASE"] as const,
  },
});
it("uses the supplied recommendation rather than deriving a primary from typed offers", async () => {
  const onAct = vi.fn();
  const value = supplied({
    kind: "ACTIONS",
    recommended: "AMEND_REGISTRATION",
    groups: [{ family: "BUSINESS", acts: ["DECIDE"] }],
  });
  const view = render(<CaseActions current={value} onAct={onAct} />);
  expect(view.container.querySelector("button.primary")).toHaveAttribute(
    "data-act",
    "AMEND_REGISTRATION",
  );
  await userEvent.setup().click(screen.getByRole("button", { name: /^Update claim details:/u }));
  expect(onAct).toHaveBeenCalledExactlyOnceWith("AMEND_REGISTRATION");
});
it("renders no inferred primary when the view supplies none", () => {
  const value = supplied({
    kind: "ACTIONS",
    recommended: null,
    groups: [{ family: "BUSINESS", acts: ["DECIDE"] }],
  });
  const view = render(<CaseActions current={value} onAct={vi.fn()} />);
  expect(view.container.querySelector("button.primary")).toBeNull();
});
it("renders no action when Core omits the recommendation and task handles", () => {
  render(
    <CaseActions
      current={{
        ...current,
        screen: { regions: [] },
        context: { ...current.context, tasks: [], correction: null },
      }}
      onAct={vi.fn()}
    />,
  );
  expect(screen.queryByRole("button")).toBeNull();
});
it.each(["primary", "alternative"] as const)(
  "rechecks the current task handle before invoking a stale %s action",
  async (kind) => {
    const value = supplied({
      kind: "ACTIONS",
      recommended: kind === "primary" ? "DECIDE" : null,
      groups: kind === "alternative" ? [{ family: "BUSINESS", acts: ["DECIDE"] }] : [],
    });
    const onAct = vi.fn();
    const props = { current: { ...value, context: { ...value.context, tasks: [] } }, onAct };
    render(
      kind === "alternative" ? <CaseActionAlternatives {...props} /> : <CaseActions {...props} />,
    );
    await userEvent.setup().click(screen.getByRole("button"));
    expect(onAct).not.toHaveBeenCalled();
  },
);
it.each([false, true])(
  "preserves the supplied stewardship group and a complete outline (inline=%s)",
  async (inline) => {
    const value = supplied({
      kind: "ACTIONS",
      recommended: null,
      groups: [{ family: "STEWARDSHIP", acts: ["VOID", "ERASE"] }],
    });
    const onAct = vi.fn();
    render(
      <section>
        <h1>Current case</h1>
        {inline ? (
          <CaseActionAlternatives current={value} onAct={onAct} />
        ) : (
          <CaseActions current={value} onAct={onAct} />
        )}
      </section>,
    );
    if (!inline) {
      await userEvent.setup().click(screen.getByText("More actions"));
    }
    expect(screen.getByRole("heading", { name: "Void and erase", level: 3 })).toBeVisible();
    expect(screen.getAllByRole("heading").map((element) => element.tagName)).toEqual([
      "H1",
      "H2",
      "H3",
    ]);
    await userEvent.setup().click(screen.getByRole("button", { name: /^Void case record:/u }));
    expect(onAct).toHaveBeenCalledExactlyOnceWith("VOID");
  },
);
