import {
  presentationLanguageOptions,
  presentationDisplayOptions,
  presentationPseudolanguage,
} from "@contracts/client";
import { useId } from "react";
import { usePresentation } from "./context";
import { resolveDisplayLocale, resolveLanguage } from "./preferences";

const preferencesStatus = (p: ReturnType<typeof usePresentation>): string => {
  if (p.persistenceFailed) {
    return p.text("ui.preferencesNotSaved");
  }
  return p.changed ? p.text("ui.preferencesChanged") : "";
};

const LanguageControl = ({ id }: { id: string }) => {
  const p = usePresentation();
  return (
    <div>
      <label htmlFor={`${id}-language`}>{p.text("ui.language")}</label>
      <select
        id={`${id}-language`}
        value={p.language}
        aria-describedby={`${id}-hint`}
        onChange={(e) => {
          p.setPreferences({
            kind: "LANGUAGE",
            language: resolveLanguage(e.target.value),
          });
        }}
      >
        {presentationLanguageOptions.map((option) => (
          <option key={option.value} value={option.value} lang={option.value}>
            {option.label}
          </option>
        ))}
        {p.language === presentationPseudolanguage ? (
          <option value={presentationPseudolanguage}>{p.text("ui.pseudo")}</option>
        ) : null}
      </select>
    </div>
  );
};

const DisplayControl = ({ id }: { id: string }) => {
  const p = usePresentation();
  return (
    <div>
      <label htmlFor={`${id}-format`}>{p.text("ui.displayLocale")}</label>
      <select
        id={`${id}-format`}
        value={p.displayLocale}
        aria-describedby={`${id}-hint`}
        onChange={(e) => {
          p.setPreferences({
            kind: "DISPLAY_LOCALE",
            displayLocale: resolveDisplayLocale(e.target.value),
          });
        }}
      >
        {presentationDisplayOptions.map((option) => (
          <option key={option.value} value={option.value} lang={option.language}>
            {option.label}
          </option>
        ))}
      </select>
    </div>
  );
};

export const PresentationControls = () => {
  const p = usePresentation();
  const id = useId();
  return (
    <section className="presentation-controls" aria-label={p.text("ui.language")}>
      <LanguageControl id={id} />
      <DisplayControl id={id} />
      <small id={`${id}-hint`}>{p.text("ui.preferencesHint")}</small>
      <span aria-live="polite" className="presentation-status">
        {preferencesStatus(p)}
      </span>
    </section>
  );
};
