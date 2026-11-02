import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { PresentationProvider } from "../../src/presentation/PresentationProvider";
import { PresentationControls } from "../../src/presentation/PresentationControls";
import { usePresentation } from "../../src/presentation/context";
import * as catalogs from "../../src/presentation/languageCatalog";

afterEach(() => {
  vi.restoreAllMocks();
});
const Probe = () => {
  const p = usePresentation();
  return (
    <>
      <PresentationControls />
      <output aria-label="Active language">{p.language}</output>
      <output aria-label="Active format">{p.displayLocale}</output>
    </>
  );
};
const view = () =>
  render(
    <PresentationProvider>
      <Probe />
    </PresentationProvider>,
  );
const coldLanguages = () =>
  vi.spyOn(catalogs, "languageLoaded").mockImplementation((language) => language === "en");

it("activates language and direction together after loading while preserving newer formatting preferences", async () => {
  coldLanguages();
  const pending = Promise.withResolvers<void>();
  vi.spyOn(catalogs, "loadLanguage").mockReturnValue(pending.promise);
  view();
  await userEvent
    .setup()
    .selectOptions(screen.getByRole("combobox", { name: "Interface language" }), "ar");
  expect(screen.getByLabelText("Active language")).toHaveTextContent("en");
  expect(document.documentElement.dir).toBe("ltr");
  await userEvent
    .setup()
    .selectOptions(screen.getByRole("combobox", { name: "Date and number format" }), "lv-LV");
  await act(async () => {
    pending.resolve();
    await pending.promise;
  });
  expect(screen.getByLabelText("Active language")).toHaveTextContent("ar");
  expect(document.documentElement.dir).toBe("rtl");
  expect(screen.getByLabelText("Active format")).toHaveTextContent("lv-LV");
});

it("keeps the working catalogs on failure and retries only on explicit request", async () => {
  coldLanguages();
  const retry = Promise.withResolvers<void>();
  const load = vi
    .spyOn(catalogs, "loadLanguage")
    .mockRejectedValueOnce(new Error("synthetic load failure"))
    .mockReturnValueOnce(retry.promise);
  view();
  await userEvent
    .setup()
    .selectOptions(screen.getByRole("combobox", { name: "Interface language" }), "lv");
  await screen.findByText("The language could not be loaded. Your working language is unchanged.");
  expect(screen.getByLabelText("Active language")).toHaveTextContent("en");
  expect(load).toHaveBeenCalledTimes(1);
  await userEvent.setup().click(screen.getByRole("button", { name: "Retry language" }));
  await act(async () => {
    retry.resolve();
    await retry.promise;
  });
  expect(screen.getByLabelText("Active language")).toHaveTextContent("lv");
  expect(load).toHaveBeenCalledTimes(2);
});

it("does not activate an obsolete language load after a newer request completes", async () => {
  coldLanguages();
  const first = Promise.withResolvers<void>();
  const second = Promise.withResolvers<void>();
  vi.spyOn(catalogs, "loadLanguage")
    .mockReturnValueOnce(first.promise)
    .mockReturnValueOnce(second.promise);
  view();
  await userEvent
    .setup()
    .selectOptions(screen.getByRole("combobox", { name: "Interface language" }), "lv");
  await userEvent
    .setup()
    .selectOptions(screen.getByRole("combobox", { name: "Interface language" }), "ar");
  await act(async () => {
    second.resolve();
    await second.promise;
  });
  await act(async () => {
    first.resolve();
    await first.promise;
  });
  await waitFor(() => {
    expect(screen.getByLabelText("Active language")).toHaveTextContent("ar");
  });
  expect(document.documentElement.dir).toBe("rtl");
});
