import { screen, within, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { render } from "../../support/presentation-test-support";
import { LoginScreen } from "../../../src/views/LoginScreen";
it("keeps preferences in the sign-in footer dialog and returns focus on close", async () => {
  const user = userEvent.setup();
  render(<LoginScreen message={null} />);
  expect(screen.queryByRole("radio")).toBeNull();
  expect(screen.queryByRole("combobox")).toBeNull();
  const trigger = screen.getByRole("button", { name: "Language & appearance" });
  await user.click(trigger);
  const dialog = screen.getByRole("dialog", { name: "Settings" });
  expect(within(dialog).getAllByRole("radio")).toHaveLength(8);
  await user.keyboard("{Escape}");
  expect(screen.queryByRole("dialog")).toBeNull();
  await waitFor(() => {
    expect(trigger).toHaveFocus();
  });
});
