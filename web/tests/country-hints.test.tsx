import { expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { DescriptorField } from "../src/components/DescriptorField";
import { definition } from "./service-ui.fixtures";
import { preferenceKey } from "../src/presentation/preferences";

const country = definition.descriptors.fields.find((field) => field.name === "incidentCountry")!;
it.each([
  ["en", "Latvia"],
  ["lv", "Latvija"],
  ["ar", "لاتفيا"],
] as const)("shows a localized country name in %s without changing its code", (language, name) => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 1, language, displayLocale: "en-GB" }),
  );
  render(<DescriptorField field={country} value="LV" onChange={vi.fn()} />);
  expect(screen.getByRole("textbox")).toHaveValue("LV");
  expect(screen.getByText(new RegExp(name, "u"))).toBeVisible();
});
it("warns for an unassigned code without blocking it and explains ZZ", () => {
  const view = render(<DescriptorField field={country} value="QQ" onChange={vi.fn()} />);
  expect(
    screen.getByText("No country name is available for QQ. Check the intended code."),
  ).toBeVisible();
  expect(screen.getByRole("textbox")).not.toHaveAttribute("aria-invalid", "true");
  view.rerender(<DescriptorField field={country} value="ZZ" onChange={vi.fn()} />);
  expect(screen.getByText("ZZ: Unknown or international")).toBeVisible();
});
