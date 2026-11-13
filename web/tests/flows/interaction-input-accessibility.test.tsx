import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../support/presentation-test-support";
import { TaskInputs } from "../../src/components/TaskInputs";
import { initialOperationValues } from "../../src/operation/operationForm";
import { operationEntry } from "../../src/protocol/operationCatalog";
import { coreCatalogs as catalogs } from "../support/authored-catalogs";
import { preferenceKey } from "../../src/presentation/preferences";
import type { Notice } from "../../src/protocol/notices";

const error = (field: "issuer" | "subject"): Notice => ({
  kind: "humanIssues",
  issues: [
    {
      input: field === "issuer" ? "input:principal.issuer" : "input:principal.subject",
      row: null,
      related: null,
      message: { messageId: "diagnostic.PRINCIPAL_FORMAT.gui", arguments: [] },
    },
  ],
});
const preferences = (language: "en" | "lv" | "ar") => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({
      version: 3,
      language,
      displayLocale: "en-GB",
      mode: "system" as const,
      accent: "green" as const,
    }),
  );
};
it.each(["en", "lv", "ar"] as const)(
  "keeps principal names stable and describes issuer/subject errors in %s",
  async (language) => {
    preferences(language);
    const user = userEvent.setup();
    const values = initialOperationValues("PEOPLE", "people.register", null);
    const props = {
      binding: { kind: "PEOPLE" as const },
      task: operationEntry({ subject: "PEOPLE", kind: "people.register" }),
      values,
      current: null,
      disabled: false,
      onChange: vi.fn(),
      onFieldsChange: vi.fn(),
    };
    const view = render(<TaskInputs {...props} errors={{}} />);
    for (const field of ["issuer", "subject"] as const) {
      const name = catalogs[language][`field.principal.${field}.label`];
      const input = screen.getByRole("textbox", { name });
      const meaning = catalogs[language][`field.principal.${field}.hint`];
      expect(input).toHaveAccessibleDescription(meaning);
      view.rerender(
        <TaskInputs {...props} errors={{ [`input:principal.${field}`]: error(field) }} />,
      );
      expect(input).toHaveAccessibleName(name);
      expect(input).toHaveAccessibleDescription(
        `${meaning} ${catalogs[language]["diagnostic.PRINCIPAL_FORMAT.gui"]}`,
      );
      expect(input).toHaveAttribute("aria-invalid", "true");
      await user.click(screen.getByText(name, { exact: true, selector: "label" }));
      expect(input).toHaveFocus();
      view.rerender(<TaskInputs {...props} errors={{}} />);
      expect(input).toHaveAccessibleName(name);
      expect(input).toHaveAccessibleDescription(meaning);
      expect(input).not.toHaveAttribute("aria-invalid");
      expect(document.getElementById(`input:principal.${field}-error`)).toBeNull();
    }
  },
);
it.each(["en", "lv", "ar"] as const)(
  "describes each correction mode with its authored group meaning in %s",
  (language) => {
    preferences(language);
    render(
      <TaskInputs
        binding={{
          kind: "CASE",
          target: { kind: "REFERENCE", caseReference: "CASE-1" },
          revision: 1,
        }}
        task={operationEntry({ subject: "CASE", kind: "case.correct" })}
        values={initialOperationValues("CASE", "case.correct", null)}
        current={null}
        disabled={false}
        errors={{}}
        onChange={vi.fn()}
        onFieldsChange={vi.fn()}
      />,
    );
    for (const name of ["registration", "decision"] as const) {
      const group = screen.getByRole("group", { name: catalogs[language][`group.${name}.label`] });
      expect(group).toHaveAccessibleDescription(catalogs[language][`group.${name}.hint`]);
      expect(group).not.toHaveAttribute("aria-invalid");
      expect(group.querySelector('input[value="KEEP"]')).toBeChecked();
    }
  },
);
