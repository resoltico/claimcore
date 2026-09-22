import { renderHook } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useEffect, useRef } from "react";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { PresentationControls } from "../src/presentation/PresentationControls";
import { usePresentation } from "../src/presentation/context";
import { Message } from "../src/presentation/Message";
import {
  defaults,
  loadPreferences,
  parsePreferences,
  preferenceKey,
  resolveDisplayLocale,
  resolveLanguage,
  savePreferences,
} from "../src/presentation/preferences";

afterEach(() => vi.restoreAllMocks());

it("resolves explicit language and display-format preferences independently with deterministic fallbacks", () => {
  for (const input of [undefined, null, 42, "", "invalid_tag", "x".repeat(65), "de-DE"])
    expect(resolveLanguage(input)).toBe("en");
  expect(resolveLanguage("LV-lv")).toBe("lv");
  expect(resolveLanguage("ar-EG")).toBe("ar");
  expect(resolveLanguage("en-xa")).toBe("en-XA");
  expect(resolveDisplayLocale("lv-lv")).toBe("lv-LV");
  expect(resolveDisplayLocale("ar-EG-u-ca-islamic")).toBe("en-GB");
  expect(resolveDisplayLocale("ar-EG")).toBe("ar-EG");
  expect(resolveDisplayLocale("de-DE")).toBe("en-GB");
  expect(
    parsePreferences(JSON.stringify({ version: 1, language: "lv-LV", displayLocale: "ar-EG" })),
  ).toEqual({ language: "lv", displayLocale: "ar-EG" });
});

it("refuses corrupt, oversized and unknown preference records without retaining arbitrary data", () => {
  for (const text of [
    null,
    "",
    "{",
    "null",
    "[]",
    "42",
    '"secret"',
    "x".repeat(257),
    '{"version":2,"language":"ar","displayLocale":"ar-EG"}',
    '{"version":1,"language":"ar"}',
    '{"version":1,"language":"ar","displayLocale":"ar-EG","claimant":"secret"}',
  ])
    expect(parsePreferences(text)).toEqual(defaults);
  expect(parsePreferences('{"version":1,"language":null,"displayLocale":42}')).toEqual(defaults);
  localStorage.setItem(preferenceKey, "private-invalid-text");
  expect(loadPreferences()).toEqual(defaults);
});

it("persists only presentation preferences and remains usable when storage is denied", () => {
  const next = { language: "lv" as const, displayLocale: "ar-EG" as const };
  expect(savePreferences(next)).toBe(true);
  expect(JSON.parse(localStorage.getItem(preferenceKey)!)).toEqual({ version: 1, ...next });
  expect(loadPreferences()).toEqual(next);
  vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
    throw new Error("denied");
  });
  vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
    throw new Error("denied");
  });
  expect(loadPreferences()).toEqual(defaults);
  expect(savePreferences(next)).toBe(false);
});

const Probe = ({ mount }: { mount: () => void }) => {
  const ref = useRef<HTMLInputElement>(null);
  useEffect(mount, [mount]);
  return (
    <>
      <input ref={ref} aria-label="unchanged draft" defaultValue="2026-02-30 / 1.0000" />
      <p>
        <Message id="ui.reference" values={{ reference: "EXACT-123" }} />
      </p>
    </>
  );
};

it("switches language and display formats without remounting children or rewriting a draft", async () => {
  const mount = vi.fn();
  const user = userEvent.setup();
  render(
    <>
      <PresentationControls />
      <Probe mount={mount} />
    </>,
  );
  const language = screen.getByLabelText("Interface language", { selector: "select" });
  const format = screen.getByLabelText("Display format", { selector: "select" });
  const input = screen.getByRole("textbox", { name: "unchanged draft" });
  await user.selectOptions(format, "lv-LV");
  await user.selectOptions(language, "ar");
  expect(document.documentElement).toHaveAttribute("lang", "ar");
  expect(document.documentElement).toHaveAttribute("dir", "rtl");
  expect(input).toBe(screen.getByRole("textbox", { name: "unchanged draft" }));
  expect(input).toHaveValue("2026-02-30 / 1.0000");
  expect(format).toHaveValue("lv-LV");
  expect(language).toHaveFocus();
  await user.selectOptions(language, "en-XA");
  expect(document.documentElement).toHaveAttribute("lang", "en");
  expect(document.documentElement).toHaveAttribute("dir", "ltr");
  expect(mount).toHaveBeenCalledOnce();
  expect(localStorage.length).toBe(1);
});

it("announces unsaved preferences in the chosen language without blocking the switch", async () => {
  vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
    throw new Error("denied-secret");
  });
  render(<PresentationControls />);
  await userEvent
    .setup()
    .selectOptions(screen.getByLabelText("Interface language", { selector: "select" }), "lv");
  expect(document.documentElement.lang).toBe("lv");
  expect(document.querySelector('[aria-live="polite"]')).not.toHaveTextContent("denied-secret");
  expect(document.querySelector('[aria-live="polite"]')?.textContent).not.toBe("");
});

it("requires an explicit presentation provider instead of silently using ambient locale", () => {
  vi.spyOn(console, "error").mockImplementation(() => undefined);
  expect(() => renderHook(usePresentation)).toThrow("A stable presentation provider is required.");
});
