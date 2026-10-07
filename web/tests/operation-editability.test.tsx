import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "./presentation-test-support";
import { deferredResponse, draftAt, editor, preparedReply } from "./presentation-state.fixtures";
import { fields, preparation, response } from "./v3-ui.fixtures";
import type { CurrentCase, Rejection } from "../src/api/v3";
import { generatedResponse } from "./contract-corpus.fixtures";

const current: CurrentCase = {
  case: { fields, revision: "1" },
  availableCommands: ["AMEND_REGISTRATION", "CORRECT_CASE", "CLOSE"],
};
const expectAuthoringDisabled = () => {
  const controls = document.querySelectorAll<HTMLInputElement | HTMLSelectElement>(
    ".operation-editor form input, .operation-editor form select",
  );
  expect(controls.length).toBeGreaterThan(1);
  for (const control of controls) {
    expect(control).toBeDisabled();
  }
};
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("shows only an accepted receipt, focuses it and keeps Return usable", async () => {
  const user = userEvent.setup();
  const committed = vi.fn();
  vi.mocked(globalThis.fetch)
    .mockImplementationOnce(() => Promise.resolve(preparedReply()))
    .mockResolvedValueOnce(generatedResponse("command.execute"));
  render(editor({ current, initialCommand: "CLOSE", onCommitted: committed }));
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.click(await screen.findByRole("checkbox", { name: "I confirm these changes." }));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
  const heading = await screen.findByRole("heading", { name: "Recorded change" });
  await waitFor(() => {
    expect(document.activeElement).toBe(heading);
  });
  expect(screen.queryByLabelText("Case action")).toBeNull();
  expect(screen.queryByRole("button", { name: "Back to case" })).toBeNull();
  await user.click(screen.getByRole("button", { name: "Return to case" }));
  expect(committed).toHaveBeenCalledOnce();
});

it.each(["AMEND_REGISTRATION", "CORRECT_CASE"] as const)(
  "reflects reducer editability during preparation, review, submission and uncertainty for %s",
  async (command) => {
    const user = userEvent.setup();
    const preparing = deferredResponse();
    const submitting = deferredResponse();
    const recover = vi.fn();
    vi.mocked(globalThis.fetch)
      .mockReturnValueOnce(preparing.promise)
      .mockReturnValueOnce(submitting.promise);
    render(editor({ current, initialCommand: command, onRecovery: recover }));
    if (command === "CORRECT_CASE") {
      await user.selectOptions(
        document.querySelector<HTMLSelectElement>("#correction-registration-mode")!,
        "REPLACE",
      );
    }
    await user.click(screen.getByRole("button", { name: "Review changes" }));
    expectAuthoringDisabled();
    preparing.resolve(preparedReply());
    await screen.findByRole("dialog", { name: "Review changes" });
    expectAuthoringDisabled();
    await user.click(screen.getByRole("checkbox", { name: "I confirm these changes." }));
    await user.click(screen.getByRole("button", { name: "Record changes" }));
    expectAuthoringDisabled();
    expect(screen.getByRole("button", { name: "Recording…" })).toBeDisabled();
    submitting.reject(new Error("Synthetic delivery loss"));
    expect(await screen.findByRole("alert")).toHaveTextContent("Inspect Recovery");
    expect(screen.queryByRole("textbox")).toBeNull();
    expect(recover).toHaveBeenCalledWith(
      expect.objectContaining({ operationId: draftAt(0).operationId }),
    );
  },
);

it("disables OPEN reference and fields after preparation uncertainty while preserving exact retry", async () => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch).mockRejectedValueOnce(new Error("Synthetic prepare loss"));
  render(editor({ current: null, initialCommand: "OPEN" }));
  await user.type(
    screen.getByLabelText("Handler's case reference", { exact: true }),
    "SYNTHETIC-OPEN",
  );
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await screen.findByRole("alert");
  expectAuthoringDisabled();
  expect(screen.getByRole("button", { name: "Retry the same review request" })).not.toBeDisabled();
  expect(draftAt(0).caseReference).toBe("SYNTHETIC-OPEN");
});

it("disables retained recovery fields and action switching without suggesting a fresh review", async () => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    response("command.prepare", "RETAINED_FOR_RECOVERY", {
      details: preparation,
      rejection: {
        code: "VERSION_CONFLICT",
        message: "Synthetic stale request.",
        diagnostic: { id: "CASE_REVISION_CONFLICT", parameters: {} },
        field: null,
        actualRevision: "2",
        recommendedAction: "READ_CURRENT",
      } satisfies Rejection,
    }),
  );
  render(editor({ current, initialCommand: "AMEND_REGISTRATION" }));
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await screen.findByRole("alert");
  expectAuthoringDisabled();
  expect(screen.getByRole("button", { name: "Review changes" })).toBeDisabled();
});
