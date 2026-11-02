import { expect, it, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { CatalogField } from "../../../src/components/CatalogField";
import { definition } from "../../support/service-ui.fixtures";
import { preferenceKey } from "../../../src/presentation/preferences";

const country = definition.catalog.fields.find((field) => field.name === "incidentCountry")!;
it.each([
  ["en", "Latvia"],
  ["lv", "Latvija"],
  ["ar", "لاتفيا"],
] as const)("shows a localized country name in %s without changing its code", (language, name) => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 2, language, displayLocale: "en-GB", theme: "SYSTEM" as const }),
  );
  render(<CatalogField field={country} value="LV" onChange={vi.fn()} />);
  expect(screen.getByRole("combobox")).toHaveValue("LV");
  expect(screen.getByText(new RegExp(name, "u"))).toBeVisible();
});
it("warns for an unassigned code without blocking it and explains ZZ", () => {
  const view = render(<CatalogField field={country} value="QQ" onChange={vi.fn()} />);
  expect(
    screen.getByText("No country name is available for QQ. Check the intended code."),
  ).toBeVisible();
  expect(screen.getByRole("combobox")).not.toHaveAttribute("aria-invalid", "true");
  view.rerender(<CatalogField field={country} value="ZZ" onChange={vi.fn()} />);
  expect(screen.getByText("ZZ: Unknown or international")).toBeVisible();
});

it("uses LF textarea and scalar guidance without UTF16 maxlength or truncation", () => {
  const field = definition.catalog.fields.find((item) => item.name === "incidentDescription")!;
  const value = "😀".repeat(600);
  const onChange = vi.fn();
  const view = render(<CatalogField field={field} value={value} onChange={onChange} />);
  const control = screen.getByRole("textbox");
  expect(control.tagName).toBe("TEXTAREA");
  expect(control).toHaveValue(value);
  expect(control).not.toHaveAttribute("maxlength");
  expect(screen.getByText("600 of 600 Unicode scalars")).toBeVisible();
  view.rerender(<CatalogField field={field} value={`${value}a`} onChange={onChange} />);
  expect(control).toHaveValue(`${value}a`);
  expect(screen.getByText("601 of 600 Unicode scalars")).toHaveAttribute("data-over-limit", "true");
  expect(onChange).not.toHaveBeenCalled();
});
it("uses distinct one-line action controls with their generated 200-scalar rule", () => {
  const field = definition.catalog.inputFields.find((item) => item.name === "reminderAction")!;
  if (field.scalar === null) {
    throw new Error("Missing action scalar owner");
  }
  render(
    <CatalogField
      field={{ ...field, scalar: field.scalar }}
      value={"😀".repeat(200)}
      onChange={vi.fn()}
    />,
  );
  const control = screen.getByRole("textbox");
  expect(control.tagName).toBe("INPUT");
  expect(control).not.toHaveAttribute("maxlength");
  expect(screen.getByText("200 of 200 Unicode scalars")).toBeVisible();
});
