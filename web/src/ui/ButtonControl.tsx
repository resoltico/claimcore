import type { ComponentProps } from "react";
import { Button as AriaButton } from "react-aria-components/Button";

export type ButtonControlProps = ComponentProps<typeof AriaButton>;

/** Retain native disabled semantics before the press responder can arm a deferred click. */
export const ButtonControl = ({ onPointerDownCapture, ...props }: ButtonControlProps) => (
  <AriaButton
    {...props}
    onPointerDownCapture={(event) => {
      if (event.currentTarget.disabled) {
        event.preventDefault();
        event.stopPropagation();
      }
      onPointerDownCapture?.(event);
    }}
  />
);
