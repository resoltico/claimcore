import { useSyncExternalStore } from "react";
const media = typeof window === "undefined" ? undefined : window.matchMedia?.("(width < 40rem)");
const snapshot = () => media?.matches ?? false;
const subscribe = (listener: () => void) => {
  media?.addEventListener("change", listener);
  return () => media?.removeEventListener("change", listener);
};
export const useCompact = () => useSyncExternalStore(subscribe, snapshot, () => true);
