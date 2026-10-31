import { caseViewInput } from "../support/inputs";
import { chooseOption } from "../support/choice-entry";
import { caseAdvice } from "../support/case-advice.fixtures";
import { render, screen } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import type { CurrentCase } from "../../src/api/service";
import { OperationEditor } from "../../src/views/OperationEditor";
import { definition, fields } from "../support/service-ui.fixtures";
import { previewReply } from "../support/presentation-state.fixtures";
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
const open = (act: "AMEND_REGISTRATION" | "CORRECT" = "AMEND_REGISTRATION") => {
  const close = vi.fn();
  render(
    <OperationEditor
      definition={definition}
      current={current}
      initialAct={act}
      onClose={close}
      onCommitted={vi.fn()}
      onMutationLockChange={vi.fn()}
    />,
  );
  return close;
};
it("leaves an untouched prefilled amendment directly", async () => {
  const user = userEvent.setup();
  const close = open();
  await user.click(
    screen.getByRole("button", {
      name: "Back to case",
    }),
  );
  expect(close).toHaveBeenCalledOnce();
  expect(screen.queryByRole("dialog")).toBeNull();
});
it("keeps a routed amendment fixed until returning to the case", async () => {
  const user = userEvent.setup();
  const close = open();
  expect(screen.queryByLabelText("Case action")).toBeNull();
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  expect(close).toHaveBeenCalledOnce();
  expect(screen.queryByRole("dialog")).toBeNull();
});
it("protects an amendment changed to all empty strings on Back", async () => {
  const user = userEvent.setup();
  const close = open();
  for (const input of screen.getAllByRole("textbox")) {
    await user.clear(input);
  }
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  expect(
    await screen.findByRole("dialog", {
      name: "Discard these draft changes?",
    }),
  ).toBeVisible();
  await user.click(
    screen.getByRole("button", {
      name: "Keep editing",
    }),
  );
  await user.click(
    screen.getByRole("button", {
      name: "Back to case",
    }),
  );
  expect(close).not.toHaveBeenCalled();
  await user.click(
    await screen.findByRole("button", {
      name: "Discard and leave",
    }),
  );
  expect(close).toHaveBeenCalledOnce();
});
it("recognizes edit then exact revert but preserves distinct amount spelling", async () => {
  const user = userEvent.setup();
  const close = open();
  const amount = screen.getByLabelText("Amount claimed", {
    exact: true,
  });
  await user.clear(amount);
  await user.type(amount, "12.3400");
  await user.click(
    screen.getByRole("button", {
      name: "Back to case",
    }),
  );
  expect(close).not.toHaveBeenCalled();
  await user.click(
    screen.getByRole("button", {
      name: "Keep editing",
    }),
  );
  await user.clear(amount);
  await user.type(amount, "12.34");
  await user.click(
    screen.getByRole("button", {
      name: "Back to case",
    }),
  );
  expect(close).toHaveBeenCalledOnce();
});
it("protects hidden correction replacement edits after returning its mode to KEEP", async () => {
  const user = userEvent.setup();
  const close = open("CORRECT");
  const mode = document.querySelector<HTMLSelectElement>("#correction-registration-mode")!;
  await chooseOption(user, mode, "REPLACE");
  await user.clear(
    screen.getByLabelText("Claimant name", {
      exact: true,
    }),
  );
  await chooseOption(user, mode, "KEEP");
  await user.click(
    screen.getByRole("button", {
      name: "Back to case",
    }),
  );
  expect(close).not.toHaveBeenCalled();
  expect(
    await screen.findByRole("dialog", {
      name: "Discard these draft changes?",
    }),
  ).toBeVisible();
});
it("leaves a correction whose mode was changed and reverted without value edits", async () => {
  const user = userEvent.setup();
  const close = open("CORRECT");
  const mode = document.querySelector<HTMLSelectElement>("#correction-payment-mode")!;
  await chooseOption(user, mode, "CLEAR");
  await chooseOption(user, mode, "KEEP");
  await user.click(
    screen.getByRole("button", {
      name: "Back to case",
    }),
  );
  expect(close).toHaveBeenCalledOnce();
});
it("protects exact authored edits after retaining the review", async () => {
  const user = userEvent.setup();
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementationOnce(() => Promise.resolve(previewReply())),
  );
  const close = open();
  await user.click(
    screen.getByRole("button", {
      name: "Review changes",
    }),
  );
  await user.click(
    await screen.findByRole("button", {
      name: "Return to editing",
    }),
  );
  const amount = screen.getByLabelText("Amount claimed", {
    exact: true,
  });
  await user.clear(amount);
  await user.type(amount, "12.3400");
  await user.click(
    screen.getByRole("button", {
      name: "Back to case",
    }),
  );
  expect(close).not.toHaveBeenCalled();
  expect(
    await screen.findByRole("dialog", {
      name: "Discard these draft changes?",
    }),
  ).toBeVisible();
  expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledOnce();
});
