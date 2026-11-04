import { act, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { render } from "../../../support/presentation-test-support";
import { SettingsPanel } from "../../../../src/features/settings/SettingsPanel";
import {
  defaults,
  loadPreferences,
  negotiatePreferences,
  parsePreferences,
  preferenceKey,
} from "../../../../src/presentation/preferences";
import * as catalogs from "../../../../src/presentation/languageCatalog";

afterEach(() => vi.restoreAllMocks());
it("negotiates the first supported browser preferences and lets stored choices win", () => {
  vi.spyOn(navigator, "languages", "get").mockReturnValue(["en-US", "lv-LV"]);
  expect(loadPreferences()).toEqual({ ...defaults, displayLocale: "en-US" });
  expect(negotiatePreferences(["unsupported", "ar-AE"])).toEqual({
    ...defaults,
    language: "ar",
    displayLocale: "ar-AE",
  });
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 3, ...defaults, language: "lv", accent: "blue" }),
  );
  expect(loadPreferences()).toEqual({ ...defaults, language: "lv", accent: "blue" });
});
it("discards v2 without converting it", () => {
  const prior = '{"version":2,"language":"ar","displayLocale":"ar-EG","theme":"DARK"}';
  expect(parsePreferences(prior)).toEqual(defaults);
  localStorage.setItem("claimcore.presentation.v2", prior);
  expect(loadPreferences()).toEqual(defaults);
});
it("applies accent and mode immediately, persists them, and announces only user changes", async () => {
  const user = userEvent.setup();
  render(<SettingsPanel />);
  const status = document.querySelector('[aria-live="polite"]');
  expect(status?.textContent).toBe("");
  await user.click(screen.getByRole("radio", { name: "Blue" }));
  expect(document.documentElement).toHaveAttribute("data-accent", "blue");
  expect(status).toHaveTextContent("Accent set to Blue.");
  await user.click(screen.getByRole("radio", { name: "Dark" }));
  expect(document.documentElement).toHaveAttribute("data-mode", "dark");
  expect(loadPreferences()).toEqual({ ...defaults, accent: "blue", mode: "dark" });
});
it("does not announce the automatic first-visit language load", async () => {
  vi.spyOn(navigator, "languages", "get").mockReturnValue(["ar-EG"]);
  vi.spyOn(catalogs, "languageLoaded").mockReturnValue(false);
  const loaded = Promise.withResolvers<void>();
  vi.spyOn(catalogs, "loadLanguage").mockReturnValue(loaded.promise);
  render(<SettingsPanel />);
  await act(async () => {
    loaded.resolve();
    await loaded.promise;
  });
  expect(document.documentElement).toHaveAttribute("dir", "rtl");
  expect(document.querySelector('[aria-live="polite"]')?.textContent).toBe("");
  expect(localStorage.getItem(preferenceKey)).toBeNull();
});
