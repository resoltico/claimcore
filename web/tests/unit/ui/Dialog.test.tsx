import { useState } from "react";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import { Dialog } from "../../../src/ui/Dialog";
import { Button } from "../../../src/ui/Button";

const Example = ({ dismissable }: { dismissable: boolean }) => {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button
        onPress={() => {
          setOpen(true);
        }}
      >
        Review
      </Button>
      <Dialog
        title="Review change"
        closeLabel="Close"
        description="Check this change."
        isOpen={open}
        isDismissable={dismissable}
        onOpenChange={setOpen}
      >
        <label>
          Draft <input defaultValue="Preserved" />
        </label>
      </Dialog>
    </>
  );
};
it("contains keyboard focus and returns it after allowed dismissal", async () => {
  const user = userEvent.setup();
  render(<Example dismissable />);
  const trigger = screen.getByRole("button", { name: "Review" });
  await user.click(trigger);
  const dialog = screen.getByRole("dialog", { name: "Review change" });
  const input = within(dialog).getByRole("textbox", { name: "Draft" });
  await waitFor(() => {
    expect(dialog).toHaveFocus();
  });
  await user.tab();
  expect(input).toHaveFocus();
  await user.tab({ shift: true });
  expect(within(dialog).getByRole("button", { name: "Close" })).toHaveFocus();
  await user.tab();
  expect(input).toHaveFocus();
  await user.keyboard("{Escape}");
  expect(screen.queryByRole("dialog")).toBeNull();
  await waitFor(() => {
    expect(trigger).toHaveFocus();
  });
});
it("does not expose dismissal or accept Escape while a protected operation is pending", async () => {
  const user = userEvent.setup();
  render(<Example dismissable={false} />);
  await user.click(screen.getByRole("button", { name: "Review" }));
  const dialog = screen.getByRole("dialog", { name: "Review change" });
  expect(within(dialog).queryByRole("button", { name: "Close" })).toBeNull();
  await user.keyboard("{Escape}");
  expect(dialog).toBeVisible();
  expect(within(dialog).getByRole("textbox", { name: "Draft" })).toHaveValue("Preserved");
});
