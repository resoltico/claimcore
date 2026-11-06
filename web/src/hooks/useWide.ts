import { useSyncExternalStore } from "react";
import { wideQuery } from "../theme/breakpoints";
const media = typeof window === "undefined" ? undefined : window.matchMedia?.(wideQuery);
const snapshot = () => media?.matches ?? false;
const subscribe = (listener: () => void) => {
  media?.addEventListener("change", listener);
  return () => {
    media?.removeEventListener("change", listener);
  };
};
export const useWide = () => useSyncExternalStore(subscribe, snapshot, () => false);
