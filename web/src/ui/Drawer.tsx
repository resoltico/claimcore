import type { ComponentProps } from "react";
import { Dialog } from "./Dialog";
import "./drawer.css";

export const Drawer = ({
  className,
  ...props
}: Omit<ComponentProps<typeof Dialog>, "placement">) => (
  <Dialog {...props} placement="end" className={["drawer", className].filter(Boolean).join(" ")} />
);
