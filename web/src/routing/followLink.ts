import type { MouseEvent } from "react";

/** Plain activation uses app navigation; browser modifiers and auxiliary buttons keep native behavior. */
export const followLink = (event: MouseEvent<HTMLAnchorElement>, action: () => void): void => {
  if (
    event.defaultPrevented ||
    event.button !== 0 ||
    event.metaKey ||
    event.ctrlKey ||
    event.shiftKey ||
    event.altKey
  ) {
    return;
  }
  event.preventDefault();
  action();
};
