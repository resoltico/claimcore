import { createContext, useContext } from "react";
import type { Route } from "../routing/routes";
import { useRoute } from "../routing/useRoute";

type SettingsDetour = {
  source: Route;
  paused: boolean;
  visit: () => void;
  permits: (destination: string | undefined) => boolean;
};
export const SettingsDetourContext = createContext<SettingsDetour | null>(null);
export const useSettingsDetour = () => useContext(SettingsDetourContext);

/** A parked People workspace keeps its selected target while Settings owns the URL. */
export const useContentRoute = () => {
  const route = useRoute();
  return useSettingsDetour()?.source ?? route;
};
