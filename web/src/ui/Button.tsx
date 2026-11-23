import "./button.css";
import { ButtonControl, type ButtonControlProps } from "./ButtonControl";

export type ButtonProps = Omit<ButtonControlProps, "className"> & {
  className?: string;
  variant?: "primary" | "secondary" | "quiet" | "danger";
  compact?: boolean;
};

export const Button = ({
  className = "",
  variant = "primary",
  compact = false,
  type = "button",
  ...props
}: ButtonProps) => (
  <ButtonControl
    {...props}
    type={type}
    className={["ui-button", `ui-button--${variant}`, compact && "ui-button--compact", className]
      .filter(Boolean)
      .join(" ")}
  />
);
