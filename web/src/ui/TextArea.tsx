import { TextArea as AriaTextArea } from "react-aria-components/TextArea";
import type { ComponentProps } from "react";
import "./field.css";

export const TextArea = ({ className, ...props }: ComponentProps<typeof AriaTextArea>) => (
  <AriaTextArea
    className={["control-box", "field-textarea", className].filter(Boolean).join(" ")}
    {...props}
  />
);
