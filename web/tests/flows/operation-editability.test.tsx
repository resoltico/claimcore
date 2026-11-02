import { selectFact } from "../support/field-entry";
import { caseViewInput } from "../support/inputs";
import { chooseOption } from "../support/choice-entry";
import { caseAdvice } from "../support/case-advice.fixtures";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { deferredResponse, editor, previewReply } from "../support/presentation-state.fixtures";
import { fields } from "../support/service-ui.fixtures";
import { type CurrentCase } from "../../src/api/service";
import { wireResponse } from "../support/contract-corpus.fixtures";
const current: CurrentCase = caseViewInput({
  caseId: "20000000-0000-4000-8000-000000000001",
  caseReference: fields.caseReference,
  revision: 1,
  advice: {
    ...caseAdvice(["AMEND_REGISTRATION", "CORRECT", "CLOSE"]),
    history: ["SUMMARY", "FULL"],
  },
  state: {
    kind: "ACTIVE",
    fields,
  },
});
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
    .mockImplementationOnce(() => Promise.resolve(previewReply()))
    .mockResolvedValueOnce(wireResponse("operation.execute"));
  render(
    editor({
      current,
      initialAct: "CLOSE",
      onCommitted: committed,
    }),
  );
  await user.click(
    screen.getByRole("button", {
      name: "Review changes",
    }),
  );
  await user.click(
    await screen.findByRole("checkbox", {
      name: "I confirm these changes.",
    }),
  );
  await user.click(
    screen.getByRole("button", {
      name: "Record changes",
    }),
  );
  const heading = await screen.findByRole("heading", {
    name: "Recorded change",
  });
  await waitFor(() => {
    expect(document.activeElement).toBe(heading);
  });
  expect(screen.queryByLabelText("Case action")).toBeNull();
  expect(
    screen.queryByRole("button", {
      name: "Back to case",
    }),
  ).toBeNull();
  await user.click(
    screen.getByRole("button", {
      name: "Return to case",
    }),
  );
  expect(committed).toHaveBeenCalledOnce();
});
it.each(["AMEND_REGISTRATION", "CORRECT"] as const)(
  "reflects reducer editability during preparation, review, submission and uncertainty for %s",
  async (act) => {
    const user = userEvent.setup();
    const preparing = deferredResponse();
    const submitting = deferredResponse();
    vi.mocked(globalThis.fetch)
      .mockReturnValueOnce(preparing.promise)
      .mockReturnValueOnce(submitting.promise);
    render(
      editor({
        current,
        initialAct: act,
      }),
    );
    if (act === "CORRECT") {
      const mode = document.querySelector<HTMLElement>("#correction-registration-mode")!;
      await chooseOption(user, mode, "UPDATE");
    }
    selectFact("claimantName");
    const claimant = screen.getByRole("textbox", { name: "Claimant name" });
    await user.clear(claimant);
    await user.type(claimant, "Edited claimant");
    await user.click(
      screen.getByRole("button", {
        name: "Review changes",
      }),
    );
    expectAuthoringDisabled();
    preparing.resolve(previewReply());
    await screen.findByRole("dialog", {
      name: "Review changes",
    });
    expectAuthoringDisabled();
    await user.click(
      screen.getByRole("checkbox", {
        name: "I confirm these changes.",
      }),
    );
    await user.click(
      screen.getByRole("button", {
        name: "Record changes",
      }),
    );
    expectAuthoringDisabled();
    expect(
      screen.getByRole("button", {
        name: "Recording…",
      }),
    ).toBeDisabled();
    submitting.reject(new Error("Synthetic delivery loss"));
    expect(await screen.findByRole("alert")).toHaveTextContent("unknown");
    expect(screen.queryByRole("textbox")).toBeNull();
  },
);
