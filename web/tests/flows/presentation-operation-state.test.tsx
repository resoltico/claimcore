import type { RequestByEndpoint } from "@contracts/client";
import { requestText, endpointResponse } from "../support/transport-response.fixtures";
import { reviewConfirm } from "../support/review-controls";
import * as catalogs from "../../src/presentation/languageCatalog";
import { changeField } from "../support/field-entry";
import { fillRegistrationInputs } from "../support/registration-inputs.fixtures";
import { act, fireEvent, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, afterEach, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import {
  current,
  deferredResponse,
  draftAt,
  editor,
  setEditorLanguage,
  previewReply,
} from "../support/presentation-state.fixtures";
import { preferenceKey } from "../../src/presentation/preferences";
import * as operationIds from "../../src/operation/operationId";
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
afterEach(() => vi.restoreAllMocks());
it("preserves authored content, text selection and operation identity across language switches before preview", async () => {
  const ids = vi.spyOn(operationIds, "operationId");
  const user = userEvent.setup();
  const pending = deferredResponse();
  vi.mocked(globalThis.fetch).mockReturnValueOnce(pending.promise);
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({
      version: 3,
      language: "en-XA",
      displayLocale: "en-GB",
      mode: "system" as const,
      accent: "green" as const,
    }),
  );
  render(
    editor({
      current: null,
      initialAct: "case.open",
    }),
  );
  fillRegistrationInputs({
    claimantName: " ",
  });
  const name = document.querySelector<HTMLInputElement>('input[data-field-name="claimantName"]')!;
  const date = document.querySelector<HTMLElement>('[data-field-name="incidentDate"]')!;
  fireEvent.change(name, {
    target: {
      value: "A\u0308 <unchanged> العربية",
    },
  });
  changeField(date, "2026-02-28");
  name.focus();
  name.setSelectionRange(1, 3);
  const before = ids.mock.calls.length;
  for (const language of ["ar", "lv", "en"]) {
    await setEditorLanguage(user, language);
  }
  expect(ids.mock.calls.length).toBe(before);
  const retainedName = screen.getByLabelText<HTMLInputElement>("Claimant name", {
    exact: true,
  });
  expect(retainedName.selectionStart).toBe(1);
  expect(retainedName.selectionEnd).toBe(3);
  expect(retainedName).toHaveValue("A\u0308 <unchanged> العربية");
  expect(document.querySelector('input[name="field:incidentDate"]')).toHaveValue("2026-02-28");
  expect(date.querySelectorAll('[role="spinbutton"]')).toHaveLength(3);
  expect(globalThis.fetch).not.toHaveBeenCalled();
  await user.click(
    screen.getByRole("button", {
      name: "Review changes",
    }),
  );
  expect(globalThis.fetch).toHaveBeenCalledOnce();
  expect(draftAt(0).task).toBe("case.open");
  expect(document.querySelector('input[name="field:incidentDate"]')).toHaveValue("2026-02-28");
});
it("finishes an in-flight language load without replaying or replacing the reviewed action", async () => {
  const user = userEvent.setup();
  const pending = deferredResponse();
  const languageReady = Promise.withResolvers<void>();
  vi.spyOn(catalogs, "languageLoaded").mockImplementation((language) => language === "en");
  vi.spyOn(catalogs, "loadLanguage").mockReturnValue(languageReady.promise);
  vi.mocked(globalThis.fetch).mockReturnValueOnce(pending.promise);
  render(editor());
  await setEditorLanguage(user, "ar");
  await user.click(
    screen.getByRole("button", {
      name: "Review changes",
    }),
  );
  const original = draftAt(0);
  pending.resolve(previewReply());
  const dialog = await screen.findByRole("dialog");
  const confirmation = reviewConfirm();
  expect(confirmation).not.toBeDisabled();
  expect(within(dialog).queryByRole("radio")).toBeNull();
  await act(async () => {
    languageReady.resolve();
    await languageReady.promise;
  });
  expect(screen.getByRole("dialog")).toBe(dialog);
  expect(reviewConfirm()).toBe(confirmation);
  expect(confirmation).not.toBeDisabled();
  expect(dialog.querySelector("button[data-confirm-operation]")).toBe(confirmation);
  expect(globalThis.fetch).toHaveBeenCalledOnce();
  expect(draftAt(0)).toEqual(original);
  expect(document.documentElement.dir).toBe("rtl");
});
it("keeps the authored revision on a fresh preview after language switching and an external revision change", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockRejectedValueOnce(new Error("lost"));
  const view = render(editor());
  await user.click(
    screen.getByRole("button", {
      name: "Review changes",
    }),
  );
  await screen.findByRole("alert");
  const initialBody = fetch.mock.calls[0]?.[1]?.body;
  await setEditorLanguage(user, "ar");
  view.rerender(
    editor({
      current: {
        ...current,
        subject: { ...current.subject, revision: 99 },
      },
    }),
  );
  await setEditorLanguage(user, "en");
  fetch.mockRejectedValueOnce(new Error("lost-again"));
  await user.click(
    screen.getByRole("button", {
      name: "Review changes",
    }),
  );
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  const first: unknown = JSON.parse(requestText(initialBody));
  const next = draftAt(1);
  const operationPreviewRequest = first as RequestByEndpoint["operation.preview"];
  const { operationId: firstId, ...firstCommand } = operationPreviewRequest;
  const { operationId: nextId, ...nextCommand } = next;
  expect(nextCommand).toEqual(firstCommand);
  expect(nextId).not.toBe(firstId);
  expect(draftAt(1).binding.expectedRevision).toBe(1);
});
it("localizes late validation by diagnostic identity and focuses the unchanged authoring field", async () => {
  const user = userEvent.setup();
  const pending = deferredResponse();
  vi.mocked(globalThis.fetch).mockReturnValueOnce(pending.promise);
  const languageReady = Promise.withResolvers<void>();
  vi.spyOn(catalogs, "languageLoaded").mockImplementation((language) => language === "en");
  vi.spyOn(catalogs, "loadLanguage").mockReturnValue(languageReady.promise);
  render(
    editor({
      current: null,
      initialAct: "case.open",
    }),
  );
  fillRegistrationInputs({
    claimantName: " ",
  });
  await setEditorLanguage(user, "lv");
  await user.click(
    screen.getByRole("button", {
      name: "Review changes",
    }),
  );
  await act(async () => {
    languageReady.resolve();
    await languageReady.promise;
  });
  pending.resolve(
    endpointResponse("operation.preview", {
      representation: "HUMAN",
      outcome: {
        kind: "REJECTED",
        rejection: {
          kind: "INPUT",
          advice: {
            message: {
              messageId: "diagnostic.INVALID_INPUT",
              arguments: [
                {
                  key: "issueCount",
                  value: {
                    kind: "COUNT",
                    value: 1,
                  },
                },
              ],
            },
            action: "CORRECT_INPUT",
          },
          issues: [
            {
              input: "field:claimantName",
              row: null,
              related: null,
              message: {
                messageId: "diagnostic.TEXT_REQUIRED",
                arguments: [],
              },
            },
          ],
        },
      },
    }),
  );
  await waitFor(() => {
    expect(screen.getByRole("alert")).toHaveFocus();
  });
  expect(document.querySelector('input[data-field-name="claimantName"]')).toHaveAttribute(
    "aria-invalid",
    "true",
  );
  expect(document.body).not.toHaveTextContent("UNTRUSTED_ENGLISH_CANARY");
  expect(screen.getByRole("alert")).toHaveTextContent("Pārbaudāmie lauki: 1.");
  expect(
    document.querySelector('input[data-field-name="claimantName"]'),
  ).toHaveAccessibleDescription(
    "Persona vai uzņēmums, kas iesniedz prasījumu. Nepieciešama netukša vērtība.",
  );
  const alert = screen.getByRole("alert").textContent;
  await setEditorLanguage(user, "en");
  expect(screen.getByRole("alert").textContent).not.toBe(alert);
  expect(screen.getByRole("alert")).toHaveTextContent("Fields to check: 1.");
  expect(
    document.querySelector('input[data-field-name="claimantName"]'),
  ).toHaveAccessibleDescription(
    "The person or company making the claim. A non-blank value is required.",
  );
  expect(document.querySelector('input[data-field-name="claimantName"]')).toHaveAttribute(
    "aria-invalid",
    "true",
  );
  expect(globalThis.fetch).toHaveBeenCalledOnce();
});
