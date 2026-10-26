import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "./presentation-test-support";
import { CatalogInputs } from "../src/components/CatalogInputs";
import { initialOperationValues } from "../src/operation/operationForm";
import { catalogs } from "@contracts/messages";
import { preferenceKey } from "../src/presentation/preferences";
import type { Notice } from "../src/protocol/notices";

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
    const values = initialOperationValues("AUTHORITY", "REGISTER", null);
    const props = { values, current: null, disabled: false, onChange: vi.fn() };
    const view = render(<CatalogInputs {...props} errorPath={null} errorNotice={null} />);
    for (const field of ["issuer", "subject"] as const) {
      const name = catalogs[language][`input.${field}.label`];
      const input = screen.getByRole("textbox", { name });
      const meaning = catalogs[language][`input.${field}.meaning`];
      expect(input).toHaveAccessibleDescription(meaning);
      view.rerender(<CatalogInputs {...props} errorPath={`target.${field}`} errorNotice={error} />);
      expect(input).toHaveAccessibleName(name);
      expect(input).toHaveAccessibleDescription(
        `${meaning} ${catalogs[language]["diagnostic.INPUT_PRINCIPAL_FORMAT"]}`,
      );
      expect(input).toHaveAttribute("aria-invalid", "true");
      await user.click(screen.getByText(name, { exact: true, selector: "label" }));
      expect(input).toHaveFocus();
      view.rerender(<CatalogInputs {...props} errorPath={null} errorNotice={null} />);
      expect(input).toHaveAccessibleName(name);
      expect(input).toHaveAccessibleDescription(meaning);
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
      <CatalogInputs
        values={initialOperationValues("CASE", "CORRECT", null)}
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
      expect(select).toHaveAccessibleName(catalogs[language]["label.action"]);
      expect(select).toHaveAccessibleDescription(catalogs[language][`group.${name}.meaning`]);
      expect(select).not.toHaveAttribute("aria-invalid");
      expect(select).toHaveValue("KEEP");
    }
  },
);
