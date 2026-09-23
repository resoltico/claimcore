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
  preparedReply,
} from "./presentation-state.fixtures";
import { response } from "./v2-ui.fixtures";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
afterEach(() => vi.restoreAllMocks());

it("preserves authored content, DOM selection and operation identity across language switches before preparation", async () => {
  const ids = vi.spyOn(crypto, "randomUUID");
  const user = userEvent.setup();
  const pending = deferredResponse();
  vi.mocked(globalThis.fetch).mockReturnValueOnce(pending.promise);
  render(editor({ current: null, initialCommand: "OPEN" }));
  const name = screen.getByLabelText<HTMLInputElement>("Claimant name", { exact: true });
  const date = screen.getByLabelText("Incident date", { exact: true });
  fireEvent.change(name, { target: { value: "A\u0308 <unchanged> العربية" } });
  fireEvent.change(date, { target: { value: "2026-02-30" } });
  name.setSelectionRange(1, 3);
  const before = ids.mock.calls.length;
  const selector = languageControl();
  for (const language of ["ar", "en-XA", "lv", "en"]) await user.selectOptions(selector, language);
  expect(ids.mock.calls.length).toBe(before);
  expect(name.selectionStart).toBe(1);
  expect(name.selectionEnd).toBe(3);
  expect(screen.getByLabelText("Claimant name", { exact: true })).toBe(name);
  expect(name).toHaveValue("A\u0308 <unchanged> العربية");
  expect(date).toHaveValue("2026-02-30");
  expect(date).toHaveAttribute("type", "text");
  expect(globalThis.fetch).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  expect(draftAt(0).command).toMatchObject({
    values: { claimantName: name.value, incidentDate: "2026-02-30" },
  });
  pending.reject(new Error("synthetic"));
  await screen.findByRole("alert");
});

it("renders a delayed preparation in the selected language without replay or confirmation loss", async () => {
  const user = userEvent.setup();
  const pending = deferredResponse();
  vi.mocked(globalThis.fetch).mockReturnValueOnce(pending.promise);
  render(editor());
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  const original = draftAt(0);
  await user.selectOptions(languageControl(), "lv");
  pending.resolve(preparedReply());
  const dialog = await screen.findByRole("dialog");
  const checkbox = within(dialog).getByRole("checkbox");
  await user.click(checkbox);
  await user.selectOptions(languageControl(), "ar");
  expect(screen.getByRole("dialog")).toBe(dialog);
  expect(checkbox).toBeChecked();
  expect(dialog).toHaveTextContent(original.operationId);
  expect(dialog).toHaveTextContent("a".repeat(64));
  expect(globalThis.fetch).toHaveBeenCalledOnce();
  expect(draftAt(0)).toEqual(original);
  expect(document.documentElement.dir).toBe("rtl");
  await user.selectOptions(languageControl(), "en");
  expect(checkbox).toBeChecked();
});

it("keeps an in-flight submission and its exact recovery identity through language and format switches", async () => {
  const user = userEvent.setup();
  const pending = deferredResponse();
  const fetch = vi.mocked(globalThis.fetch);
  fetch
    .mockImplementationOnce(() => Promise.resolve(preparedReply()))
    .mockReturnValueOnce(pending.promise);
  render(editor());
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  const dialog = await screen.findByRole("dialog");
  await user.click(within(dialog).getByRole("checkbox"));
  await user.click(screen.getByRole("button", { name: "Submit exact request" }));
  const submitted = fetch.mock.calls[1]?.[1];
  await user.selectOptions(languageControl(), "ar");
  await user.keyboard("{Escape}");
  expect(screen.getByRole("dialog")).toBe(dialog);
  expect(within(dialog).getByRole("checkbox")).toBeChecked();
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(fetch.mock.calls[1]?.[1]).toBe(submitted);
  pending.reject(new Error("PRIVATE_PROVIDER_CANARY"));
  await screen.findByRole("alert");
  expect(document.body).not.toHaveTextContent("PRIVATE_PROVIDER_CANARY");
  await user.selectOptions(languageControl(), "en");
  expect(screen.getByRole("alert")).toHaveTextContent(
    "Inspect Recovery before taking another action.",
  );
  expect(screen.getByRole("button", { name: "Back without preparing" })).toBeDisabled();
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(JSON.parse(typeof submitted?.body === "string" ? submitted.body : "")).toEqual({
    operationId: draftAt(0).operationId,
    requestSha256: "a".repeat(64),
  });
});

it("does not rebase a frozen preparation after language switching and an external revision change", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockRejectedValueOnce(new Error("lost"));
  const view = render(editor());
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  await screen.findByRole("alert");
  const initialBody = fetch.mock.calls[0]?.[1]?.body;
  await user.selectOptions(languageControl(), "ar");
  view.rerender(editor({ current: { ...current, case: { ...current.case, revision: "99" } } }));
  await user.selectOptions(languageControl(), "en");
  fetch.mockRejectedValueOnce(new Error("lost-again"));
  await user.click(screen.getByRole("button", { name: "Retry exact prepare" }));
  await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2));
  expect(fetch.mock.calls[1]?.[1]?.body).toBe(initialBody);
  expect(draftAt(1).expectedRevision).toBe("1");
});

it("localizes late validation by diagnostic identity and focuses the unchanged authoring field", async () => {
  const user = userEvent.setup();
  const pending = deferredResponse();
  vi.mocked(globalThis.fetch).mockReturnValueOnce(pending.promise);
  render(editor({ current: null, initialCommand: "OPEN" }));
  const name = screen.getByLabelText<HTMLInputElement>("Claimant name", { exact: true });
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  await user.selectOptions(languageControl(), "lv");
  pending.resolve(
    response("command.prepare", "REJECTED", {
      operationId: draftAt(0).operationId,
      rejection: {
        code: "INVALID_INPUT",
        message: "UNTRUSTED_ENGLISH_CANARY",
        field: "claimantName",
        actualRevision: null,
        recommendedAction: "CORRECT_INPUT",
        diagnostic: { id: "INPUT_TEXT_REQUIRED", parameters: {} },
      },
    }),
  );
  await waitFor(() => expect(name).toHaveFocus());
  expect(name).toHaveAttribute("aria-invalid", "true");
  expect(document.body).not.toHaveTextContent("UNTRUSTED_ENGLISH_CANARY");
  const alert = screen.getByRole("alert").textContent;
  await user.selectOptions(languageControl(), "en");
  expect(screen.getByRole("alert").textContent).not.toBe(alert);
  expect(screen.getByRole("alert")).toHaveTextContent("A non-blank value is required.");
  expect(globalThis.fetch).toHaveBeenCalledOnce();
});
