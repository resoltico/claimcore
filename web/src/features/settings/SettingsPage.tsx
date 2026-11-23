import { useEffect } from "react";
import { OwnBrowserSessions } from "./BrowserSessions";
import { AboutPanel } from "./AboutPanel";
import { SettingsPanel } from "./SettingsPanel";
import { usePresentation } from "../../presentation/context";
import { Page } from "../../ui/Page";
import { Section } from "../../ui/Section";
import { useRoute } from "../../routing/useRoute";
import "./settings.css";

const sections = [
  ["settings-language-region", "ui.languageRegion"],
  ["settings-appearance", "ui.appearance"],
  ["settings-browser-sessions", "ui.browserSessions"],
  ["settings-about", "ui.about"],
] as const;

export const SettingsPage = () => {
  const p = usePresentation();
  const route = useRoute();
  useEffect(() => {
    if (
      route.screen === "settings" &&
      new URLSearchParams(window.location.search).get("section") === "about"
    ) {
      document.getElementById("settings-about")?.scrollIntoView({ block: "nearest" });
    }
  }, [route]);
  return (
    <div className="settings-workspace">
      <Page
        id="settings-page"
        title={p.text("ui.settings")}
        tabs={
          <nav aria-label={p.text("ui.settings")} className="settings-navigation">
            {sections.map(([id, message]) => (
              <a key={id} href={`#${id}`}>
                {p.text(message)}
              </a>
            ))}
          </nav>
        }
      >
        <SettingsPanel idPrefix="settings" />
        <Section id="settings-browser-sessions" title={p.text("ui.browserSessions")}>
          <OwnBrowserSessions />
        </Section>
        <Section id="settings-about" title={p.text("ui.about")}>
          <AboutPanel />
        </Section>
      </Page>
    </div>
  );
};
