import { afterEach, expect, it, vi } from "vitest";
import core from "../../../../src/ClaimCore.Presentation/Messages/Localization/en.json" with { type: "json" };
import chrome from "../../../locales/en.json" with { type: "json" };

afterEach(() => vi.unstubAllGlobals());
it("fetches the resolved same-origin pair without credentials and admits it before activation", async () => {
  vi.resetModules();
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValue(
      new Response(JSON.stringify({ core, chrome }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
  vi.stubGlobal("fetch", fetch);
  const catalogs = await import("../../../src/presentation/languageCatalog");
  await catalogs.loadLanguage("en");
  const options = fetch.mock.calls[0]?.[1];
  expect(options?.signal).toBeInstanceOf(AbortSignal);
  expect(fetch).toHaveBeenCalledExactlyOnceWith("/catalogs/en.json", {
    method: "GET",
    credentials: "omit",
    redirect: "error",
    signal: options?.signal,
    cache: "no-cache",
    headers: { Accept: "application/json" },
  });
  expect(catalogs.catalogFor("en")["ui.loading"]).toBe("Loading…");
  expect(catalogs.catalogFor("en")["act.OPEN.label"]).toBe("Open a case");
});
it("refuses an incomplete served pair while keeping the complete working language", async () => {
  vi.resetModules();
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ core, chrome }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ core, chrome: {} }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      ),
  );
  const catalogs = await import("../../../src/presentation/languageCatalog");
  await catalogs.loadLanguage("en");
  await expect(catalogs.loadLanguage("lv")).rejects.toThrow("Incomplete language catalog");
  expect(catalogs.languageLoaded("lv")).toBe(false);
  expect(catalogs.catalogFor("en")["ui.loading"]).toBe("Loading…");
});
