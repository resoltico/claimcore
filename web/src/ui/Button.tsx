import "./button.css";
import type { ComponentProps } from "react";
import { Button as AriaButton } from "react-aria-components/Button";

export type ButtonProps = Omit<ComponentProps<typeof AriaButton>, "className"> & {
  className?: string;
  variant?: "primary" | "secondary" | "danger";
};

export const Button = ({
  className = "",
  variant = "primary",
  type = "button",
  ...props
}: ButtonProps) => (
  <AriaButton
    {...props}
    type={type}
    className={["ui-button", `ui-button--${variant}`, className].filter(Boolean).join(" ")}
  />
);
