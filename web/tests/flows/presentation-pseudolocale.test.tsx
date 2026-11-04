import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { SettingsPanel } from "../../src/features/settings/SettingsPanel";
import { preferenceKey } from "../../src/presentation/preferences";

it("offers real languages normally and a truthful current diagnostic option only when seeded", async () => {
  const user = userEvent.setup();
  const normal = render(<SettingsPanel />);
  expect(document.querySelector('option[value="en-XA"]')).toBeNull();
  normal.unmount();
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({
      version: 3,
      language: "en-XA",
      displayLocale: "ar-EG",
      mode: "system" as const,
      accent: "green" as const,
    }),
  );
  render(<SettingsPanel />);
  const language = document.querySelector('input[value="en-XA"]');
  expect(language).toBeChecked();
  expect(document.querySelector('input[value="en-XA"]')).not.toBeNull();
  await user.click(screen.getByRole("radio", { name: "Latviešu" }));
  expect(document.querySelector('input[value="en-XA"]')).toBeNull();
  expect(screen.getByRole("combobox")).toHaveValue("ar-EG");
});
