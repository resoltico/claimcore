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
