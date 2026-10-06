import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { render, screen } from "./presentation-test-support";
import { PresentationControls } from "../src/presentation/PresentationControls";
import { preferenceKey } from "../src/presentation/preferences";

it("offers real languages normally and a truthful current diagnostic option only when seeded", async () => {
  const user = userEvent.setup();
  const normal = render(<PresentationControls />);
  expect(document.querySelector('option[value="en-XA"]')).toBeNull();
  normal.unmount();
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 1, language: "en-XA", displayLocale: "ar-EG" }),
  );
  render(<PresentationControls />);
  const language = document.querySelector<HTMLSelectElement>('select[id$="-language"]')!;
  expect(language).toHaveValue("en-XA");
  expect(language.querySelector('option[value="en-XA"]')).not.toBeNull();
  await user.selectOptions(language, "lv");
  expect(language).toHaveValue("lv");
  expect(language.querySelector('option[value="en-XA"]')).toBeNull();
  expect(screen.getAllByRole("combobox")[1]).toHaveValue("ar-EG");
});
