import { SettingsPanel } from "./SettingsPanel";
import { usePresentation } from "../../presentation/context";
import { useSessionContext } from "../../session/SessionContext";
import { guardedDeparture } from "../../routing/navigation";
export const SettingsPage = () => {
  const p = usePresentation();
  const { logout } = useSessionContext();
  return (
    <section>
      <h1 tabIndex={-1}>{p.text("ui.settings")}</h1>
      <SettingsPanel />
      <button
        type="button"
        onClick={() => {
          guardedDeparture(() => {
            void logout();
          });
        }}
      >
        {p.text("ui.signOut")}
      </button>
    </section>
  );
};
