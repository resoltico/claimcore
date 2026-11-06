import { act, fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { Button } from "../../../src/ui/Button";
import { ButtonControl } from "../../../src/ui/ButtonControl";

it.each([Button, ButtonControl])(
  "a pointer sequence begun while disabled cannot submit after reenabling (%#)",
  (Control) => {
    vi.useFakeTimers();
    const submit = vi.fn();
    const view = (disabled: boolean) => (
      <form
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <Control type="submit" isDisabled={disabled}>
          Review
        </Control>
      </form>
    );
    try {
      const rendered = render(view(true));
      const button = screen.getByRole("button", { name: "Review" });
      expect(button).toBeDisabled();
      fireEvent.pointerDown(button, { button: 0, pointerId: 1, pointerType: "mouse" });
      fireEvent.pointerUp(button, { button: 0, pointerId: 1, pointerType: "mouse" });
      expect(submit).not.toHaveBeenCalled();
      rendered.rerender(view(false));
      expect(button).not.toBeDisabled();
      act(() => {
        vi.advanceTimersByTime(100);
      });
      expect(submit).not.toHaveBeenCalled();
      fireEvent.click(button);
      expect(submit).toHaveBeenCalledOnce();
    } finally {
      vi.useRealTimers();
    }
  },
);
