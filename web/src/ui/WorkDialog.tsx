import type { ComponentProps } from "react";
import { Dialog } from "./Dialog";
import "./work-dialog.css";

export const WorkDialog = ({
  className = "",
  ...props
}: Omit<ComponentProps<typeof Dialog>, "placement" | "overlayClassName" | "scrollableBody">) => (
  <Dialog
    {...props}
    scrollableBody
    overlayClassName="work-dialog-overlay"
    className={
      props.anchorRef === undefined
        ? ["work-dialog", className].filter(Boolean).join(" ")
        : className
    }
  />
);
