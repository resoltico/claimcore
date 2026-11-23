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
  // Some browsers leave pointer-activated links unfocused. Record the clicked origin for Back.
  if (event.currentTarget.id !== "") {
    event.currentTarget.focus({ preventScroll: true });
  }
  action();
};
