import { expect, it, vi, afterEach } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { App } from "../../src/shell/App";
import { navigate } from "../../src/routing/navigation";
import { preferenceKey } from "../../src/presentation/preferences";
afterEach(() => {
  vi.unstubAllGlobals();
  navigate("/cases", true);
});
it.each([
  ["en", "Page not found"],
  ["lv", "Lapa nav atrasta"],
  ["ar", "الصفحة غير موجودة"],
])("shows the localized anonymous unknown-route page in %s", async (language, title) => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({
      version: 3,
      language,
      displayLocale: "en-GB",
      mode: "system",
      accent: "green" as const,
    }),
  );
  const fetch = vi.fn<typeof globalThis.fetch>();
  vi.stubGlobal("fetch", fetch);
  navigate("/unknown-page/deep?keep=1", true);
  render(<App />);
  const heading = await screen.findByRole("heading", { name: title, level: 1 });
  expect(heading).toBeVisible();
  expect(heading).toHaveFocus();
  await waitFor(() => {
    expect(document.title).toBe(`${title} · ClaimCore`);
  });
  expect(window.location.pathname + window.location.search).toBe("/unknown-page/deep?keep=1");
  expect(fetch.mock.calls.map(([path]) => path)).toEqual(["/api/session"]);
  expect(screen.getByRole("main").querySelector("a")).toHaveAttribute("href", "/cases");
});
