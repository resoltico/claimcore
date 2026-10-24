import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { within } from "@testing-library/react";
import { useOperation } from "../src/hooks/operation/useOperation";
import { OperationPanel, OperationDiscard } from "../src/views/operation/OperationPanel";
const locked = vi.fn();
const Workspace = ({ epoch }: { epoch: number }) => {
  const model = useOperation({
    scope: "AUTHORITY",
    kind: "REGISTER_ACTOR",
    current: null,
    token: "synthetic",
    onLockChange: locked,
  });
  return (
    <>
      <OperationDiscard model={model} />
      <OperationPanel key={epoch} model={model} current={null} offered={["REGISTER_ACTOR"]} />
    </>
  );
};
it("preserves an open discard warning when a readonly workspace epoch is replaced", async () => {
  const user = userEvent.setup();
  const view = render(<Workspace epoch={0} />);
  await user.type(
    screen.getByRole("textbox", { name: "Subject or client ID" }),
    "synthetic-authored",
  );
  await user.click(screen.getByRole("button", { name: "Discard and leave" }));
  expect(screen.getByRole("dialog")).toBeVisible();
  view.rerender(<Workspace epoch={1} />);
  const dialog = screen.getByRole("dialog");
  expect(dialog).toBeVisible();
  await user.click(within(dialog).getByRole("button", { name: "Keep editing" }));
  expect(screen.getByRole("textbox", { name: "Subject or client ID" })).toHaveValue(
    "synthetic-authored",
  );
  await user.click(screen.getByRole("button", { name: "Discard and leave" }));
  await user.click(
    within(screen.getByRole("dialog")).getByRole("button", { name: "Discard and leave" }),
  );
  expect(screen.getByRole("textbox", { name: "Subject or client ID" })).toHaveValue("");
  expect(locked).toHaveBeenLastCalledWith(false);
});
