import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../support/presentation-test-support";
import { CatalogInputs } from "../../src/components/CatalogInputs";
import { initialOperationValues, operationEntry } from "../../src/operation/operationForm";
import { catalogs } from "@contracts/messages";
import { preferenceKey } from "../../src/presentation/preferences";
import type { Notice } from "../../src/protocol/notices";

const error = (field: "issuer" | "subject"): Notice => ({
  kind: "issues",
  issues: [{ field: `principal.${field}`, code: "PRINCIPAL_FORMAT" }],
});
const preferences = (language: "en" | "lv" | "ar") => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 2, language, displayLocale: "en-GB", theme: "SYSTEM" as const }),
  );
};
it.each(["en", "lv", "ar"] as const)(
  "keeps principal names stable and describes issuer/subject errors in %s",
  async (language) => {
    preferences(language);
    const user = userEvent.setup();
    const values = initialOperationValues("AUTHORITY", "REGISTER", null);
    const props = {
      entry: operationEntry({ subject: "AUTHORITY", kind: "REGISTER" }),
      values,
      current: null,
      disabled: false,
      onChange: vi.fn(),
    };
    const view = render(<CatalogInputs {...props} fieldErrors={{}} />);
    for (const field of ["issuer", "subject"] as const) {
      const name = catalogs[language][`field.principal.${field}.label`];
      const input = screen.getByRole("textbox", { name });
      const meaning = catalogs[language][`field.principal.${field}.meaning`];
      expect(input).toHaveAccessibleDescription(meaning);
      view.rerender(
        <CatalogInputs {...props} fieldErrors={{ [`target.${field}`]: error(field) }} />,
      );
      expect(input).toHaveAccessibleName(name);
      expect(input).toHaveAccessibleDescription(
        `${meaning} ${catalogs[language]["diagnostic.PRINCIPAL_FORMAT"]}`,
      );
      expect(input).toHaveAttribute("aria-invalid", "true");
      await user.click(screen.getByText(name, { exact: true, selector: "label" }));
      expect(input).toHaveFocus();
      view.rerender(<CatalogInputs {...props} fieldErrors={{}} />);
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
        entry={operationEntry({ subject: "CASE", kind: "CORRECT" })}
        values={initialOperationValues("CASE", "CORRECT", null)}
        current={null}
        disabled={false}
        fieldErrors={{}}
        onChange={vi.fn()}
      />,
    );
    for (const name of ["registration", "decision", "payment"] as const) {
      const group = screen.getByRole("group", { name: catalogs[language][`group.${name}.label`] });
      const select = group.querySelector<HTMLElement>('[role="radiogroup"]')!;
      expect(select).toHaveAccessibleName(catalogs[language]["label.action"]);
      expect(select).toHaveAccessibleDescription(catalogs[language][`group.${name}.meaning`]);
      expect(select).not.toHaveAttribute("aria-invalid");
      expect(select.querySelector('input[value="KEEP"]')).toBeChecked();
    }
  },
);
