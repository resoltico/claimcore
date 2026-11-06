import { useSyncExternalStore } from "react";
const media =
  typeof window === "undefined" ? undefined : window.matchMedia?.("(prefers-color-scheme: dark)");
const snapshot = () => media?.matches ?? false;
const subscribe = (listener: () => void) => {
  media?.addEventListener("change", listener);
  return () => media?.removeEventListener("change", listener);
};
export const useSystemMode = () =>
  useSyncExternalStore(subscribe, snapshot, () => false) ? "dark" : "light";
