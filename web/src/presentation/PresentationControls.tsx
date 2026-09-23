import { useId } from "react";
import { usePresentation } from "./context";
import { resolveDisplayLocale, resolveLanguage } from "./preferences";

export const PresentationControls = () => {
  const p = usePresentation();
  const id = useId();
  return (
    <section className="presentation-controls" aria-label={p.text("ui.language")}>
      <LanguageControl id={id} />
      <DisplayControl id={id} />
      <small id={`${id}-hint`}>{p.text("ui.preferencesHint")}</small>
      <span aria-live="polite" className="presentation-status">
        {p.persistenceFailed
          ? p.text("ui.preferencesNotSaved")
          : p.changed
            ? p.text("ui.preferencesChanged")
            : ""}
      </span>
    </section>
  );
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
        onChange={(e) =>
          p.setPreferences({
            language: resolveLanguage(e.target.value),
            displayLocale: p.displayLocale,
          })
        }
      >
        <option value="en" lang="en">
          English
        </option>
        <option value="lv" lang="lv">
          Latviešu
        </option>
        <option value="ar" lang="ar">
          العربية
        </option>
        <option value="en-XA">{p.text("ui.pseudo")}</option>
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
        onChange={(e) =>
          p.setPreferences({
            language: p.language,
            displayLocale: resolveDisplayLocale(e.target.value),
          })
        }
      >
        <option value="en-GB" lang="en">
          English — United Kingdom
        </option>
        <option value="lv-LV" lang="lv">
          Latviešu — Latvija
        </option>
        <option value="ar-EG" lang="ar">
          العربية — مصر
        </option>
      </select>
    </div>
  );
};
