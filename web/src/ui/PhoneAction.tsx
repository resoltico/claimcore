import { createContext, useContext, type ReactNode } from "react";
import { createPortal } from "react-dom";

export const PhoneActionTarget = createContext<HTMLElement | null>(null);

/** Keep one action control per screen and move it to the phone bar when the bar is mounted. */
export const PhoneAction = ({
  children,
  renderCompact,
}: {
  children: ReactNode;
  renderCompact: () => ReactNode;
}) => {
  const target = useContext(PhoneActionTarget);
  return target === null ? children : createPortal(renderCompact(), target);
};
