import { useSyncExternalStore } from "react";
import { compactQuery } from "../theme/breakpoints";
const media = typeof window === "undefined" ? undefined : window.matchMedia?.(compactQuery);
const snapshot = () => media?.matches ?? false;
const subscribe = (listener: () => void) => {
  media?.addEventListener("change", listener);
  return () => media?.removeEventListener("change", listener);
};
export const useCompact = () => useSyncExternalStore(subscribe, snapshot, () => true);
