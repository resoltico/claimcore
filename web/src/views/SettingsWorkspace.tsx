import { useState, type ReactNode } from "react";
import { SettingsDetourContext } from "../hooks/SettingsDetourContext";
import { SettingsPage } from "../features/settings/SettingsPage";
import { Button } from "../ui/Button";
import { navigate, routeSnapshot } from "../routing/navigation";
import type { Route } from "../routing/routes";
import { usePresentation } from "../presentation/context";

/** Settings owns the screen while the same scoped operation tree stays parked. */
export const SettingsWorkspace = ({
  route,
  children,
}: {
  route: Route;
  children: (source: Route) => ReactNode;
}) => {
  const p = usePresentation();
  const [source, setSource] = useState({ route, path: routeSnapshot() });
  const settings = route.screen === "settings";
  if (!settings && source.route !== route) {
    setSource({ route, path: routeSnapshot() });
  }
  const parked = settings && source.route.screen !== "settings";
  const contentRoute = parked ? source.route : route;
  const contentPath = settings ? source.path : routeSnapshot();
  return (
    <SettingsDetourContext
      value={{
        source: contentRoute,
        paused: parked,
        visit: () => {
          navigate("/settings");
        },
        permits: (destination) => destination === "/settings" || destination === contentPath,
      }}
    >
      <div hidden={parked} inert={parked}>
        {children(contentRoute)}
      </div>
      {parked ? (
        <>
          <Button
            variant="secondary"
            data-return-workspace
            onPress={() => {
              navigate(source.path);
            }}
          >
            {p.text("ui.returnToWorkspace")}
          </Button>
          <SettingsPage />
        </>
      ) : null}
    </SettingsDetourContext>
  );
};
