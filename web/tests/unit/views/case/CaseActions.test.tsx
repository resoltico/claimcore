import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { current } from "../../../support/operation-form.fixtures";
import { CaseActions, CaseActionAlternatives } from "../../../../src/views/case/CaseActions";
import { RequesterContext } from "../../../../src/hooks/useRequesterContext";
import { context } from "../../../support/service-ui.fixtures";
import { initialMaintenance } from "../../../support/inputs";
import type { RegionContent } from "@contracts/client";

const supplied = (content: Extract<RegionContent, { kind: "ACTIONS" }>) => ({
  ...current,
  view: { regions: [{ role: "RECOMMENDATION" as const, content }] },
});
it("uses the supplied recommendation rather than deriving a primary from typed offers", async () => {
  const onAct = vi.fn();
  const value = supplied({
    kind: "ACTIONS",
    recommended: "AMEND_REGISTRATION",
    groups: [{ family: "BUSINESS", acts: ["DECIDE"] }],
  });
  const view = render(
    <CaseActions
      current={{ ...value, advice: { ...value.advice, recommended: "DECIDE" } }}
      onAct={onAct}
    />,
  );
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
it.each(["snapshot", "requester"] as const)(
  "keeps the %s maintenance gate on the supplied recommendation",
  (gate) => {
    const active = { active: true, generation: 1 };
    const value = supplied({ kind: "ACTIONS", recommended: "DECIDE", groups: [] });
    render(
      <RequesterContext
        value={{ ...context, maintenance: gate === "requester" ? active : initialMaintenance }}
      >
        <CaseActions
          current={{
            ...value,
            advice: {
              ...value.advice,
              maintenance: gate === "snapshot" ? active : initialMaintenance,
            },
          }}
          onAct={vi.fn()}
        />
      </RequesterContext>,
    );
    expect(screen.queryByRole("button")).toBeNull();
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
