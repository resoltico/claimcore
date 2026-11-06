import "./button.css";
import { ButtonControl, type ButtonControlProps } from "./ButtonControl";

export type ButtonProps = Omit<ButtonControlProps, "className"> & {
  className?: string;
  variant?: "primary" | "secondary" | "danger";
};

export const Button = ({
  className = "",
  variant = "primary",
  type = "button",
  ...props
}: ButtonProps) => (
  <ButtonControl
    {...props}
    type={type}
    className={["ui-button", `ui-button--${variant}`, className].filter(Boolean).join(" ")}
  />
);
