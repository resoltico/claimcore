import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "./presentation-test-support";
import { InteractionInputs } from "../src/components/InteractionInputs";
import { initialOperationValues } from "../src/interaction/operationForm";
import { catalogs } from "../src/presentation/generated/catalogs";
import { preferenceKey } from "../src/presentation/preferences";
import type { Notice } from "../src/api/notices";

const error: Notice = {
  kind: "diagnostic",
  diagnostic: { code: "INPUT_PRINCIPAL_FORMAT", limits: {} },
};
const preferences = (language: "en" | "lv" | "ar") => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 1, language, displayLocale: "en-GB" }),
  );
};
it.each(["en", "lv", "ar"] as const)(
  "keeps principal names stable and describes issuer/subject errors in %s",
  async (language) => {
    preferences(language);
    const user = userEvent.setup();
    const values = initialOperationValues("AUTHORITY", "REGISTER_ACTOR", null);
    const props = { values, current: null, disabled: false, onChange: vi.fn() };
    const view = render(<InteractionInputs {...props} errorPath={null} errorNotice={null} />);
    for (const field of ["issuer", "subject"] as const) {
      const name = catalogs[language][`authority.field.${field}.label`];
      const input = screen.getByRole("textbox", { name });
      expect(input).not.toHaveAccessibleDescription();
      view.rerender(
        <InteractionInputs {...props} errorPath={`target.${field}`} errorNotice={error} />,
      );
      expect(input).toHaveAccessibleName(name);
      expect(input).toHaveAccessibleDescription(
        catalogs[language]["diagnostic.INPUT_PRINCIPAL_FORMAT"],
      );
      expect(input).toHaveAttribute("aria-invalid", "true");
      await user.click(screen.getByText(name, { exact: true, selector: "label" }));
      expect(input).toHaveFocus();
      view.rerender(<InteractionInputs {...props} errorPath={null} errorNotice={null} />);
      expect(input).toHaveAccessibleName(name);
      expect(input).not.toHaveAccessibleDescription();
      expect(input).not.toHaveAttribute("aria-invalid");
      expect(document.getElementById(`target.${field}-error`)).toBeNull();
    }
  },
);
it.each(["en", "lv", "ar"] as const)(
  "describes each correction mode with its authored group meaning in %s",
  (language) => {
    preferences(language);
    render(
      <InteractionInputs
        values={initialOperationValues("CASE", "CORRECT_CASE", null)}
        current={null}
        disabled={false}
        errorPath={null}
        errorNotice={null}
        onChange={vi.fn()}
      />,
    );
    for (const name of ["registration", "decision", "payment"] as const) {
      const group = screen.getByRole("group", { name: catalogs[language][`group.${name}.label`] });
      const select = group.querySelector("select");
      expect(select).toHaveAccessibleName(catalogs[language]["ui.action"]);
      expect(select).toHaveAccessibleDescription(catalogs[language][`group.${name}.meaning`]);
      expect(select).not.toHaveAttribute("aria-invalid");
      expect(select).toHaveValue("KEEP");
    }
  },
);
