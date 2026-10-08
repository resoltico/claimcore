import { render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/v3";
import { OperationEditor } from "../src/views/OperationEditor";
import { definition, fields } from "./v3-ui.fixtures";
import { preparedReply } from "./presentation-state.fixtures";

const current: CurrentCase = {
  case: { fields, revision: "1" },
  availableCommands: ["AMEND_REGISTRATION", "CORRECT_CASE", "CLOSE"],
};
const open = (command: "AMEND_REGISTRATION" | "CORRECT_CASE" = "AMEND_REGISTRATION") => {
  const close = vi.fn();
  render(
    <OperationEditor
      token="synthetic"
      definition={definition}
      current={current}
      initialCommand={command}
      onClose={close}
      onCommitted={vi.fn()}
      onRecovery={vi.fn()}
      onMutationLockChange={vi.fn()}
    />,
  );
  return close;
};

it("leaves an untouched prefilled amendment directly", async () => {
  const user = userEvent.setup();
  const close = open();
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  expect(close).toHaveBeenCalledOnce();
  expect(screen.queryByRole("dialog")).toBeNull();
});

it("switches an untouched amendment without discarding authored work", async () => {
  const user = userEvent.setup();
  open();
  await user.selectOptions(screen.getByLabelText("Case action"), "CLOSE");
  expect(screen.getByRole("heading", { name: "Close the case" })).toBeVisible();
  expect(screen.queryByRole("dialog")).toBeNull();
});

it("protects an amendment changed to all empty strings on switch and Back", async () => {
  const user = userEvent.setup();
  const close = open();
  for (const input of screen.getAllByRole("textbox")) {
    await user.clear(input);
  }
  await user.selectOptions(screen.getByLabelText("Case action"), "CLOSE");
  expect(await screen.findByRole("dialog", { name: "Discard these draft changes?" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Keep editing" }));
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  expect(close).not.toHaveBeenCalled();
  await user.click(await screen.findByRole("button", { name: "Discard and leave" }));
  expect(close).toHaveBeenCalledOnce();
});

it("recognizes edit then exact revert but preserves distinct amount spelling", async () => {
  const user = userEvent.setup();
  const close = open();
  const amount = screen.getByLabelText("Amount claimed", { exact: true });
  await user.clear(amount);
  await user.type(amount, "12.3400");
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  expect(close).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Keep editing" }));
  await user.clear(amount);
  await user.type(amount, "12.34");
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  expect(close).toHaveBeenCalledOnce();
});

it("protects hidden correction replacement edits after returning its mode to KEEP", async () => {
  const user = userEvent.setup();
  const close = open("CORRECT_CASE");
  const mode = document.querySelector<HTMLSelectElement>("#correction-registration-mode")!;
  await user.selectOptions(mode, "REPLACE");
  await user.clear(screen.getByLabelText("Claimant name", { exact: true }));
  await user.selectOptions(mode, "KEEP");
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  expect(close).not.toHaveBeenCalled();
  expect(await screen.findByRole("dialog", { name: "Discard these draft changes?" })).toBeVisible();
});

it("leaves a correction whose mode was changed and reverted without value edits", async () => {
  const user = userEvent.setup();
  const close = open("CORRECT_CASE");
  const mode = document.querySelector<HTMLSelectElement>("#correction-payment-mode")!;
  await user.selectOptions(mode, "CLEAR");
  await user.selectOptions(mode, "KEEP");
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  expect(close).toHaveBeenCalledOnce();
});

it("leaves an unchanged retained review without stopping its server request", async () => {
  const user = userEvent.setup();
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementationOnce(() => Promise.resolve(preparedReply())),
  );
  const close = open();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.click(
    await screen.findByRole("button", { name: "Back to editing; keep for Recovery" }),
  );
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  expect(close).toHaveBeenCalledOnce();
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledOnce();
});

it("protects exact authored edits after retaining the review", async () => {
  const user = userEvent.setup();
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementationOnce(() => Promise.resolve(preparedReply())),
  );
  const close = open();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.click(
    await screen.findByRole("button", { name: "Back to editing; keep for Recovery" }),
  );
  const amount = screen.getByLabelText("Amount claimed", { exact: true });
  await user.clear(amount);
  await user.type(amount, "12.3400");
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  expect(close).not.toHaveBeenCalled();
  expect(await screen.findByRole("dialog", { name: "Discard these draft changes?" })).toBeVisible();
  expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledOnce();
});
