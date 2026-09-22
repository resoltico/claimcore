import "./styles.css";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import { I18nProvider } from "react-aria-components/I18nProvider";
import { PresentationContext } from "./context";
import { createPresenter } from "./presenter";
import { loadPreferences, savePreferences, type Preferences } from "./preferences";

export const PresentationProvider = ({ children }: { children: ReactNode }) => {
  const [preferences, setValue] = useState(loadPreferences);
  const [persistenceFailed, setPersistenceFailed] = useState(false);
  const [changed, setChanged] = useState(false);
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
    setPreferences: (next: Preferences): void => {
      setValue(next);
      setPersistenceFailed(!savePreferences(next));
      setChanged(true);
    },
  };
  return (
    <PresentationContext value={value}>
      <I18nProvider locale={language}>{children}</I18nProvider>
    </PresentationContext>
  );
};
