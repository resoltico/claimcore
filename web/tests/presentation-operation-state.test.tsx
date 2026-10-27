import { fillRegistrationInputs } from "./registration-inputs.fixtures";
import { fireEvent, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, afterEach, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "./presentation-test-support";
import {
  current,
  deferredResponse,
  draftAt,
  editor,
  languageControl,
  previewReply,
} from "./presentation-state.fixtures";
import { preferenceKey } from "../src/presentation/preferences";
import { response } from "./service-ui.fixtures";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
afterEach(() => vi.restoreAllMocks());

it("preserves authored content, DOM selection and operation identity across language switches before preview", async () => {
  const ids = vi.spyOn(crypto, "randomUUID");
  const user = userEvent.setup();
  const pending = deferredResponse();
  vi.mocked(globalThis.fetch).mockReturnValueOnce(pending.promise);
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 1, language: "en-XA", displayLocale: "en-GB" }),
  );
  render(editor({ current: null, initialAct: "OPEN" }));
  fillRegistrationInputs({ claimantName: " " });
  const name = document.querySelector<HTMLInputElement>('input[data-field-name="claimantName"]')!;
  const date = document.querySelector<HTMLInputElement>('input[data-field-name="incidentDate"]')!;
  fireEvent.change(name, { target: { value: "A\u0308 <unchanged> العربية" } });
  fireEvent.change(date, { target: { value: "2026-02-30" } });
  name.setSelectionRange(1, 3);
  const before = ids.mock.calls.length;
  const selector = languageControl();
  for (const language of ["ar", "lv", "en"]) {
    await user.selectOptions(selector, language);
  }
  expect(ids.mock.calls.length).toBe(before);
  expect(name.selectionStart).toBe(1);
  expect(name.selectionEnd).toBe(3);
  expect(screen.getByLabelText("Claimant name", { exact: true })).toBe(name);
  expect(name).toHaveValue("A\u0308 <unchanged> العربية");
  expect(date).toHaveValue("2026-02-30");
  expect(date).toHaveAttribute("type", "text");
  expect(globalThis.fetch).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  expect(globalThis.fetch).toHaveBeenCalledOnce();
  expect(draftAt(0).act.kind).toBe("OPEN");
  expect(date).toHaveValue("2026-02-30");
});

it("renders a delayed review in the selected language without replay or confirmation loss", async () => {
  const user = userEvent.setup();
  const pending = deferredResponse();
  vi.mocked(globalThis.fetch).mockReturnValueOnce(pending.promise);
  render(editor());
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  const original = draftAt(0);
  await user.selectOptions(languageControl(), "lv");
  pending.resolve(previewReply());
  const dialog = await screen.findByRole("dialog");
  const checkbox = within(dialog).getByRole("checkbox");
  await user.click(checkbox);
  await user.selectOptions(languageControl(), "ar");
  expect(screen.getByRole("dialog")).toBe(dialog);
  expect(checkbox).toBeChecked();
  expect(dialog).toHaveTextContent(original.operationId);
  expect(globalThis.fetch).toHaveBeenCalledOnce();
  expect(draftAt(0)).toEqual(original);
  expect(document.documentElement.dir).toBe("rtl");
  await user.selectOptions(languageControl(), "en");
  expect(checkbox).toBeChecked();
});

it("does not rebase a frozen request after language switching and an external revision change", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockRejectedValueOnce(new Error("lost"));
  const view = render(editor());
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await screen.findByRole("alert");
  const initialBody = fetch.mock.calls[0]?.[1]?.body;
  await user.selectOptions(languageControl(), "ar");
  view.rerender(editor({ current: { ...current, revision: 99 } }));
  await user.selectOptions(languageControl(), "en");
  fetch.mockRejectedValueOnce(new Error("lost-again"));
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  expect(fetch.mock.calls[1]?.[1]?.body).toBe(initialBody);
  expect(draftAt(1).expectedRevision).toBe(1);
});

it("localizes late validation by diagnostic identity and focuses the unchanged authoring field", async () => {
  const user = userEvent.setup();
  const pending = deferredResponse();
  vi.mocked(globalThis.fetch).mockReturnValueOnce(pending.promise);
  render(editor({ current: null, initialAct: "OPEN" }));
  fillRegistrationInputs({ claimantName: " " });
  const name = screen.getByLabelText<HTMLInputElement>("Claimant name", { exact: true });
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.selectOptions(languageControl(), "lv");
  pending.resolve(
    response("operation.preview", "REJECTED", {
      code: "TEXT_REQUIRED",
      action: "CORRECT_INPUT",
      target: "claimantName",
    }),
  );
  await waitFor(() => {
    expect(name).toHaveFocus();
  });
  expect(name).toHaveAttribute("aria-invalid", "true");
  expect(document.body).not.toHaveTextContent("UNTRUSTED_ENGLISH_CANARY");
  const alert = screen.getByRole("alert").textContent;
  await user.selectOptions(languageControl(), "en");
  expect(screen.getByRole("alert").textContent).not.toBe(alert);
  expect(screen.getByRole("alert")).toHaveTextContent("A non-blank value is required.");
  expect(globalThis.fetch).toHaveBeenCalledOnce();
});
