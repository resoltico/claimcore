import { useMemo, useSyncExternalStore } from "react";
import { parseRoute } from "./routes";
import { routeSnapshot, subscribeRoute } from "./navigation";
export const useRoute = () => {
  const path = useSyncExternalStore(subscribeRoute, routeSnapshot, () => "/cases");
  return useMemo(() => parseRoute(new URL(path, window.location.origin)), [path]);
};
