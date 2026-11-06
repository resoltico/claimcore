import { act, fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { Button } from "../../../src/ui/Button";

it("a pointer sequence begun while disabled cannot submit after reenabling", () => {
  vi.useFakeTimers();
  const submit = vi.fn();
  const view = (disabled: boolean) => (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <Button type="submit" isDisabled={disabled}>
        Review
      </Button>
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
});
