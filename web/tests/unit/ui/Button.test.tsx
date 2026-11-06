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

it.each([Button, ButtonControl])(
  "a pointer sequence begun in a disabled fieldset cannot activate after reenabling (%#)",
  (Control) => {
    vi.useFakeTimers();
    const submit = vi.fn();
    const press = vi.fn();
    const capture = vi.fn();
    const view = (disabled: boolean) => (
      <form
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <fieldset disabled={disabled}>
          <Control type="submit" onPress={press} onPointerDownCapture={capture}>
            Review
          </Control>
        </fieldset>
      </form>
    );
    try {
      const rendered = render(view(true));
      const button = screen.getByRole("button", { name: "Review" });
      expect(button).toHaveProperty("disabled", false);
      expect(button).toBeDisabled();
      fireEvent.pointerDown(button, { button: 0, pointerId: 1, pointerType: "mouse" });
      fireEvent.pointerUp(button, { button: 0, pointerId: 1, pointerType: "mouse" });
      expect(capture).toHaveBeenCalledOnce();
      expect(press).not.toHaveBeenCalled();
      expect(submit).not.toHaveBeenCalled();
      rendered.rerender(view(false));
      expect(button).not.toBeDisabled();
      act(() => {
        vi.advanceTimersByTime(100);
      });
      expect(press).not.toHaveBeenCalled();
      expect(submit).not.toHaveBeenCalled();
      fireEvent.click(button);
      expect(press).toHaveBeenCalledOnce();
      expect(submit).toHaveBeenCalledOnce();
    } finally {
      vi.useRealTimers();
    }
  },
);

it.each([Button, ButtonControl])(
  "the first legend of a disabled fieldset keeps enabled pointer activation (%#)",
  (Control) => {
    vi.useFakeTimers();
    const press = vi.fn();
    const submit = vi.fn();
    const pointer = vi.fn();
    try {
      render(
        <form
          onSubmit={(event) => {
            event.preventDefault();
            submit();
          }}
        >
          <fieldset disabled>
            <legend>
              <Control
                type="submit"
                onPress={press}
                onPointerDownCapture={(event) => {
                  pointer(event.defaultPrevented, event.isPropagationStopped());
                }}
              >
                Legend action
              </Control>
            </legend>
          </fieldset>
        </form>,
      );
      const button = screen.getByRole("button", { name: "Legend action" });
      expect(button).not.toBeDisabled();
      fireEvent.pointerDown(button, { button: 0, pointerId: 1, pointerType: "mouse" });
      expect(pointer).toHaveBeenCalledOnce();
      expect(pointer).toHaveBeenCalledWith(false, false);
      fireEvent.pointerUp(button, { button: 0, pointerId: 1, pointerType: "mouse" });
      fireEvent.click(button);
      act(() => {
        vi.advanceTimersByTime(100);
      });
      expect(press).toHaveBeenCalledOnce();
      expect(submit).toHaveBeenCalledOnce();
    } finally {
      vi.useRealTimers();
    }
  },
);
