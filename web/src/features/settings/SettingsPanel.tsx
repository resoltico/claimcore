import { useSystemMode } from "../../theme/useSystemMode";
import "./settings.css";
import { useId } from "react";
import {
  presentationLanguageOptions,
  presentationDisplayOptions,
  presentationPseudolanguage,
} from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import {
  resolveDisplayLocale,
  resolveLanguage,
  modes,
  accents,
} from "../../presentation/preferences";
import { productMetadata } from "../../protocol/productMetadata";

const modeLabels = {
  system: "ui.themeSystem",
  light: "ui.themeLight",
  dark: "ui.themeDark",
} as const;
const accentLabels = { green: "ui.accentGreen", blue: "ui.accentBlue" } as const;
const preferencesStatus = (p: ReturnType<typeof usePresentation>): string => {
  if (p.persistenceFailed) {
    return p.text("ui.preferencesNotSaved");
  }
  const change = p.changed;
  if (change === null) {
    return "";
  }
  switch (change.kind) {
    case "LANGUAGE":
      return p.text("ui.languageSet", {
        value:
          presentationLanguageOptions.find((option) => option.value === change.language)?.label ??
          change.language,
      });
    case "DISPLAY_LOCALE":
      return p.text("ui.displayLocaleSet", {
        value:
          presentationDisplayOptions.find((option) => option.value === change.displayLocale)
            ?.label ?? change.displayLocale,
      });
    case "MODE":
      return p.text("ui.modeSet", { value: p.text(modeLabels[change.mode]) });
    case "ACCENT":
      return p.text("ui.accentSet", { value: p.text(accentLabels[change.accent]) });
  }
};
export const SettingsPanel = () => {
  const p = usePresentation();
  const id = useId();
  const systemMode = useSystemMode();
  const options =
    p.language === presentationPseudolanguage
      ? [
          ...presentationLanguageOptions,
          { value: presentationPseudolanguage, label: p.text("ui.pseudo"), direction: "ltr" },
        ]
      : presentationLanguageOptions;
  return (
    <section className="settings-panel" aria-label={p.text("ui.settings")}>
      <p>{p.text("ui.settingsIntroduction")}</p>
      <section className="settings-card">
        <h2>{p.text("ui.languageRegion")}</h2>
        <fieldset className="settings-options">
          <legend>{p.text("ui.language")}</legend>
          {options.map((option) => (
            <label key={option.value} className="settings-option" lang={option.value}>
              <input
                type="radio"
                name={`${id}-language`}
                value={option.value}
                checked={(p.requestedLanguage ?? p.language) === option.value}
                onChange={() => {
                  p.setPreferences({ kind: "LANGUAGE", language: resolveLanguage(option.value) });
                }}
              />
              <span dir={option.direction}>
                {option.label}
                {p.detectedLanguage && p.language === option.value ? (
                  <small aria-hidden="true">{p.text("ui.detectedLanguage")}</small>
                ) : null}
                {option.direction === "rtl" ? (
                  <small aria-hidden="true">{p.text("ui.rightToLeft")}</small>
                ) : null}
              </span>
            </label>
          ))}
        </fieldset>
        {p.requestedLanguage === null ? null : <p role="status">{p.text("ui.loadingLanguage")}</p>}
        {p.languageFailure === null ? null : (
          <div role="alert">
            <p>{p.text("ui.languageLoadFailed")}</p>
            <button type="button" onClick={p.retryLanguage}>
              {p.text("ui.retryLanguage")}
            </button>
          </div>
        )}
        <label htmlFor={`${id}-format`}>{p.text("ui.displayLocale")}</label>
        <select
          id={`${id}-format`}
          value={p.displayLocale}
          onChange={(event) => {
            p.setPreferences({
              kind: "DISPLAY_LOCALE",
              displayLocale: resolveDisplayLocale(event.target.value),
            });
          }}
        >
          {presentationDisplayOptions.map((option) => (
            <option key={option.value} value={option.value} lang={option.language}>
              {option.label}
            </option>
          ))}
        </select>
        <p className="settings-preview">
          {p.text("ui.formatPreview", {
            date: p.date("2026-11-04"),
            amount: p.money("1234.50", "EUR"),
          })}
        </p>
      </section>
      <section className="settings-card">
        <h2>{p.text("ui.appearance")}</h2>
        <fieldset className="settings-options">
          <legend>{p.text("ui.theme")}</legend>
          {modes.map((mode) => (
            <label key={mode} className="settings-option">
              <input
                type="radio"
                name={`${id}-mode`}
                value={mode}
                checked={p.mode === mode}
                onChange={() => {
                  p.setPreferences({ kind: "MODE", mode });
                }}
              />
              <span>
                {p.text(modeLabels[mode])}
                {mode === "system" ? (
                  <small aria-hidden="true">
                    {p.text("ui.currentMode", { value: p.text(modeLabels[systemMode]) })}
                  </small>
                ) : null}
              </span>
            </label>
          ))}
        </fieldset>
        <fieldset className="settings-options settings-accents">
          <legend>{p.text("ui.accent")}</legend>
          {accents.map((accent) => (
            <label key={accent} className="settings-option accent-card">
              <span className="accent-preview" data-accent={accent} aria-hidden="true">
                <span />
                <i />
                <i />
              </span>
              <span className="accent-choice">
                <input
                  type="radio"
                  name={`${id}-accent`}
                  value={accent}
                  checked={p.accent === accent}
                  onChange={() => {
                    p.setPreferences({ kind: "ACCENT", accent });
                  }}
                />
                <span>{p.text(accentLabels[accent])}</span>
              </span>
              {accent === "green" ? <small>{p.text("ui.defaultAccent")}</small> : null}
            </label>
          ))}
        </fieldset>
        <p>{p.text("ui.themeAccessibility")}</p>
      </section>
      <section className="settings-card">
        <h2>{p.text("ui.about")}</h2>
        <dl>
          <dt>{p.text("ui.version")}</dt>
          <dd>{productMetadata.productVersion}</dd>
          <dt>{p.text("ui.contract")}</dt>
          <dd>
            <bdi className="mono">{productMetadata.contractIdentity}</bdi>
          </dd>
        </dl>
      </section>
      <span aria-live="polite" className="presentation-status">
        {preferencesStatus(p)}
      </span>
    </section>
  );
};
