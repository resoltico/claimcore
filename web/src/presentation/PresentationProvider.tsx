import "./styles.css";
import { guardedDeparture } from "../routing/navigation";
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { I18nProvider } from "react-aria-components/I18nProvider";
import { PresentationContext } from "./context";
import { createPresenter } from "./presenter";
import { catalogLanguage, languageLoaded, loadLanguage } from "./languageCatalog";
import { bootMessages } from "../generated/chrome-boot";
import {
  loadPreferences,
  savePreferences,
  changePreferences,
  languageDirection,
  type PreferenceChange,
  type Preferences,
  type Language,
} from "./preferences";

type State = {
  preferences: Preferences;
  ready: boolean;
  pending: Language | null;
  failed: Language | null;
  persistenceFailed: boolean;
  changed: boolean;
};
const activate = (current: State, language: Language): State => {
  const preferences = changePreferences(current.preferences, { kind: "LANGUAGE", language });
  return {
    ...current,
    preferences,
    ready: true,
    pending: null,
    failed: null,
    persistenceFailed: !savePreferences(preferences),
    changed: true,
  };
};
export const PresentationProvider = ({ children }: { children: ReactNode }) => {
  const initial = useMemo(() => loadPreferences(), []);
  const [state, setState] = useState<State>(() => ({
    preferences: initial,
    ready: languageLoaded(initial.language),
    pending: languageLoaded(initial.language) ? null : initial.language,
    failed: null,
    persistenceFailed: false,
    changed: false,
  }));
  const generation = useRef(0);
  const beginLoad = useCallback((language: Language, identity: number) => {
    void loadLanguage(language)
      .then(() => {
        if (identity === generation.current) {
          setState((current) => activate(current, language));
        }
      })
      .catch(() => {
        if (identity === generation.current) {
          setState((current) => ({ ...current, pending: null, failed: language }));
        }
      });
  }, []);
  const requestLanguage = useCallback(
    (language: Language) => {
      const identity = ++generation.current;
      if (languageLoaded(language)) {
        setState((current) => activate(current, language));
        return;
      }
      setState((current) => ({ ...current, pending: language, failed: null }));
      beginLoad(language, identity);
    },
    [beginLoad],
  );
  useEffect(() => {
    if (!languageLoaded(initial.language)) {
      beginLoad(initial.language, ++generation.current);
    }
    return () => {
      generation.current += 1;
    };
  }, [initial.language, beginLoad]);
  const { preferences } = state;
  const presenter = useMemo(
    () => (state.ready ? createPresenter(preferences) : null),
    [preferences, state.ready],
  );
  useEffect(() => {
    document.documentElement.lang = catalogLanguage(preferences.language);
    document.documentElement.dir = languageDirection(preferences.language);
    if (preferences.theme === "SYSTEM") {
      delete document.documentElement.dataset["theme"];
    } else {
      document.documentElement.dataset["theme"] = preferences.theme.toLowerCase();
    }
  }, [preferences]);
  const setPreferences = (change: PreferenceChange): void => {
    if (change.kind === "LANGUAGE") {
      requestLanguage(change.language);
      return;
    }
    setState((current) => {
      const next = changePreferences(current.preferences, change);
      return {
        ...current,
        preferences: next,
        persistenceFailed: !savePreferences(next),
        changed: true,
      };
    });
  };
  const retryLanguage = () => {
    if (state.failed !== null) {
      requestLanguage(state.failed);
    }
  };
  if (presenter === null) {
    const boot = bootMessages[catalogLanguage(preferences.language)];
    return (
      <main className="loading" role="status">
        <h1>ClaimCore</h1>
        <p>{boot.loading}</p>
        {state.failed === null ? (
          <progress aria-label="ClaimCore" />
        ) : (
          <div className="actions">
            <button type="button" onClick={retryLanguage}>
              {boot.retry}
            </button>
            <button
              type="button"
              onClick={() => {
                guardedDeparture(() => {
                  window.location.reload();
                });
              }}
            >
              {boot.reload}
            </button>
          </div>
        )}
      </main>
    );
  }
  return (
    <PresentationContext
      value={{
        ...presenter,
        setPreferences,
        persistenceFailed: state.persistenceFailed,
        changed: state.changed,
        requestedLanguage: state.pending,
        languageFailure: state.failed,
        retryLanguage,
      }}
    >
      <I18nProvider locale={preferences.displayLocale}>{children}</I18nProvider>
    </PresentationContext>
  );
};
