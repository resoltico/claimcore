import "./styles.css";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import { I18nProvider } from "react-aria-components/I18nProvider";
import { PresentationContext } from "./context";
import { createPresenter } from "./presenter";
import {
  loadPreferences,
  savePreferences,
  changePreferences,
  type PreferenceChange,
} from "./preferences";

export const PresentationProvider = ({ children }: { children: ReactNode }) => {
  const [state, setState] = useState(() => ({
    preferences: loadPreferences(),
    persistenceFailed: false,
    changed: false,
  }));
  const { preferences, persistenceFailed, changed } = state;
  const presenter = useMemo(() => createPresenter(preferences), [preferences]);
  const language = preferences.language === "en-XA" ? "en" : preferences.language;
  useEffect(() => {
    document.documentElement.lang = language;
    document.documentElement.dir = presenter.direction;
    document.title = "ClaimCore";
  }, [language, presenter.direction]);
  const value = {
    ...presenter,
    persistenceFailed,
    changed,
    setPreferences: (change: PreferenceChange): void => {
      setState((current) => {
        const next = changePreferences(current.preferences, change);
        return { preferences: next, persistenceFailed: !savePreferences(next), changed: true };
      });
    },
  };
  return (
    <PresentationContext value={value}>
      <I18nProvider locale={language}>{children}</I18nProvider>
    </PresentationContext>
  );
};
