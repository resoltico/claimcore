import { createContext, useContext } from "react";
import type { createPresenter } from "./presenter";
import type { PreferenceChange } from "./preferences";
export type Presentation = ReturnType<typeof createPresenter> & {
  setPreferences: (change: PreferenceChange) => void;
  persistenceFailed: boolean;
  changed: boolean;
};
export const PresentationContext = createContext<Presentation | null>(null);
export const usePresentation = (): Presentation => {
  const value = useContext(PresentationContext);
  if (value === null) {
    throw new Error("A stable presentation provider is required.");
  }
  return value;
};
