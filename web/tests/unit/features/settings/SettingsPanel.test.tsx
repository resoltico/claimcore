import { act, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { render } from "../../../support/presentation-test-support";
import { SettingsPanel } from "../../../../src/features/settings/SettingsPanel";
import {
  defaults,
  loadPreferenceState,
  negotiatePreferences,
  preferenceKey,
} from "../../../../src/presentation/preferences";
import * as catalogs from "../../../../src/presentation/languageCatalog";

afterEach(() => vi.restoreAllMocks());
it("negotiates the first supported browser preferences and lets stored choices win", () => {
  vi.spyOn(navigator, "languages", "get").mockReturnValue(["en-US", "lv-LV"]);
  expect(loadPreferenceState().preferences).toEqual({ ...defaults, displayLocale: "en-US" });
  expect(negotiatePreferences(["unsupported", "ar-AE"])).toEqual({
    ...defaults,
    language: "ar",
    displayLocale: "ar-AE",
  });
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 3, ...defaults, language: "lv", accent: "blue" }),
  );
  expect(loadPreferenceState().preferences).toEqual({
    ...defaults,
    language: "lv",
    accent: "blue",
  });
});
it("shows incompatible saved settings and requires explicit reset before persisting changes", async () => {
  const user = userEvent.setup();
  const prior = '{"version":2,"language":"ar","displayLocale":"ar-EG","theme":"DARK"}';
  const retired = "claimcore.presentation.v2";
  const incompatible = '{"version":4}';
  localStorage.setItem(retired, prior);
  localStorage.setItem(preferenceKey, incompatible);
  localStorage.setItem("unrelated", "unchanged");
  render(<SettingsPanel />);

  expect(screen.getByRole("alert")).toHaveTextContent("cannot read your saved preferences");
  await user.click(screen.getByRole("radio", { name: "Green" }));
  expect(document.documentElement).toHaveAttribute("data-accent", "green");
  expect(localStorage.getItem(retired)).toBe(prior);
  expect(localStorage.getItem(preferenceKey)).toBe(incompatible);

  await user.click(screen.getByRole("button", { name: "Reset saved preferences" }));
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  expect(localStorage.getItem(retired)).toBeNull();
  expect(localStorage.getItem("unrelated")).toBe("unchanged");
  expect(loadPreferenceState().preferences).toEqual({ ...defaults, accent: "green" });
});
it("applies accent and mode immediately, persists them, and announces only user changes", async () => {
  const user = userEvent.setup();
  render(<SettingsPanel />);
  const status = document.querySelector('[aria-live="polite"]');
  expect(status?.textContent).toBe("");
  expect(screen.getByRole("radio", { name: /Blue/u })).toBeChecked();
  expect(document.documentElement).toHaveAttribute("data-accent", "blue");
  await user.click(screen.getByRole("radio", { name: "Green" }));
  expect(document.documentElement).toHaveAttribute("data-accent", "green");
  expect(status).toHaveTextContent("Accent set to Green.");
  await user.click(screen.getByRole("radio", { name: "Dark" }));
  expect(document.documentElement).toHaveAttribute("data-mode", "dark");
  expect(loadPreferenceState().preferences).toEqual({ ...defaults, accent: "green", mode: "dark" });
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
