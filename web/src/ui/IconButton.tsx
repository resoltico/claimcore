import { Button, type ButtonProps } from "./Button";
import { Icon, type IconName } from "./icons";

type Props = Omit<ButtonProps, "children" | "aria-label"> & {
  icon: IconName;
  label: string;
};

export const IconButton = ({
  icon,
  label,
  className = "",
  variant = "secondary",
  ...props
}: Props) => (
  <Button
    {...props}
    variant={variant}
    aria-label={label}
    className={["ui-icon-button", className].filter(Boolean).join(" ")}
  >
    <Icon name={icon} />
  </Button>
);
