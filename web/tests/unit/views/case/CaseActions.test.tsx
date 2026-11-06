import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { current } from "../../../support/operation-form.fixtures";
import {
  CaseActions,
  CaseActionAlternatives,
  CasePrimaryAction,
} from "../../../../src/views/case/CaseActions";
import { RequesterContext } from "../../../../src/hooks/useRequesterContext";
import { context } from "../../../support/service-ui.fixtures";
import { initialMaintenance } from "../../../support/inputs";

it.each([
  ["DECIDE", "AMEND_REGISTRATION"],
  ["AMEND_REGISTRATION", "DECIDE"],
] as const)(
  "renders the supplied recommendation independently of offer order %s/%s",
  async (...acts) => {
    const onAct = vi.fn();
    const view = render(
      <CasePrimaryAction
        current={{
          ...current,
          advice: { ...current.advice, acts, recommended: "AMEND_REGISTRATION" },
        }}
        onAct={onAct}
      />,
    );
    expect(view.container.querySelector("button.primary")).toHaveAttribute(
      "data-act",
      "AMEND_REGISTRATION",
    );
    await userEvent.setup().click(screen.getByRole("button"));
    expect(onAct).toHaveBeenCalledExactlyOnceWith("AMEND_REGISTRATION");
  },
);
it("renders no inferred primary when the core supplies no recommendation", () => {
  render(
    <CasePrimaryAction
      current={{ ...current, advice: { ...current.advice, acts: ["DECIDE"], recommended: null } }}
      onAct={vi.fn()}
    />,
  );
  expect(screen.queryByRole("button")).toBeNull();
});
it.each(["snapshot", "requester"] as const)(
  "keeps the %s maintenance gate on the supplied recommendation",
  (gate) => {
    const active = { active: true, generation: 1 };
    render(
      <RequesterContext
        value={{ ...context, maintenance: gate === "requester" ? active : initialMaintenance }}
      >
        <CasePrimaryAction
          current={{
            ...current,
            advice: {
              ...current.advice,
              acts: ["DECIDE"],
              recommended: "DECIDE",
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

const compact = vi.hoisted(() => ({ value: false }));
vi.mock("../../../../src/hooks/useCompact", () => ({ useCompact: () => compact.value }));

it.each([
  ["desktop More actions", false, false, 2],
  ["compact More actions", true, false, 3],
  ["inline unavailable-act alternatives", false, true, 2],
] as const)(
  "keeps stewardship headings in the actual %s outline",
  async (_name, narrow, alternatives, level) => {
    compact.value = narrow;
    try {
      const onAct = vi.fn();
      const supplied = {
        ...current,
        advice: { ...current.advice, acts: ["VOID", "ERASE"] as const, recommended: null },
      };
      render(
        <section>
          <h1>Current case</h1>
          {alternatives ? (
            <CaseActionAlternatives current={supplied} onAct={onAct} />
          ) : (
            <CaseActions current={supplied} onAct={onAct} />
          )}
        </section>,
      );
      const user = userEvent.setup();
      if (!alternatives) {
        await user.click(screen.getByText("More actions"));
      }
      expect(screen.getByRole("heading", { name: "Stewardship", level })).toBeVisible();
      expect(screen.getAllByRole("heading").map((element) => element.tagName)).toEqual(
        narrow ? ["H1", "H2", "H3"] : ["H1", "H2"],
      );
      await user.click(screen.getByRole("button", { name: /^Void case record:/u }));
      expect(onAct).toHaveBeenCalledExactlyOnceWith("VOID");
      expect(screen.getByRole("button", { name: /^Erase case:/u })).toBeVisible();
    } finally {
      compact.value = false;
    }
  },
);
