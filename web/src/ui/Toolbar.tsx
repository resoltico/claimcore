import type { ReactNode } from "react";
import "./toolbar.css";

export const Toolbar = ({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) => <div className={["toolbar", className].filter(Boolean).join(" ")}>{children}</div>;
