import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { IconButton } from "../../../src/ui/IconButton";
import { IconSprite } from "../../../src/ui/icons";
it("keeps a decorative SVG separate from its control's accessible name and press behavior", async () => {
  const pressed = vi.fn();
  render(
    <>
      <IconSprite />
      <IconButton icon="calendar" label="Open calendar" onPress={pressed} />
    </>,
  );
  const button = screen.getByRole("button", { name: "Open calendar" });
  expect(button).toHaveAttribute("type", "button");
  expect(button.querySelector("svg")).toHaveAttribute("aria-hidden", "true");
  expect(button.querySelector("use")).toHaveAttribute("href", "#claimcore-icon-calendar");
  await userEvent.setup().click(button);
  expect(pressed).toHaveBeenCalledOnce();
});
