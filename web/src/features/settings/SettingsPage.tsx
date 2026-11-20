import { OwnBrowserSessions } from "./BrowserSessions";
import { Button } from "../../ui/Button";
import { SettingsPanel } from "./SettingsPanel";
import { usePresentation } from "../../presentation/context";
import { useSessionContext } from "../../session/SessionContext";
import "./settings.css";
import { guardedDeparture } from "../../routing/navigation";
export const SettingsPage = () => {
  const p = usePresentation();
  const { logout } = useSessionContext();
  return (
    <section>
      <h1 tabIndex={-1}>{p.text("ui.settings")}</h1>
      <SettingsPanel />
      <section className="settings-session-group" aria-labelledby="current-session">
        <h2 id="current-session">{p.text("ui.currentBrowserSession")}</h2>
        <Button
          variant="secondary"
          onPress={() => {
            guardedDeparture(() => {
              void logout();
            });
          }}
        >
          {p.text("ui.signOut")}
        </Button>
      </section>
      <section className="settings-session-group" aria-labelledby="browser-sessions">
        <h2 id="browser-sessions">{p.text("ui.browserSessions")}</h2>
        <OwnBrowserSessions />
      </section>
    </section>
  );
};
