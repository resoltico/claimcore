import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { current } from "../../../support/operation-form.fixtures";
import { CasePrimaryAction } from "../../../../src/views/case/CaseActions";
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
