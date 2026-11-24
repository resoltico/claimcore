import { TextField as AriaTextField } from "react-aria-components/TextField";
import { Input } from "react-aria-components/Input";
import type { ComponentProps } from "react";
import "./field.css";

export const TextInput = ({ className, ...props }: ComponentProps<typeof Input>) => (
  <Input className={["control-box", className].filter(Boolean).join(" ")} {...props} />
);

export const ControlInput = Input;

export const ChoiceControl = (props: ComponentProps<"input"> & { type: "checkbox" | "radio" }) => (
  <input {...props} />
);

export const TextField = AriaTextField;
