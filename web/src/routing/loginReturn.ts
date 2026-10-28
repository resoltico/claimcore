import { navigate } from "./navigation";
const key = "claimcore.loginReturn";
const sameOriginPath = (path: string): boolean => {
  try {
    const target = new URL(path, window.location.origin);
    return (
      path.startsWith("/") && !path.startsWith("//") && target.origin === window.location.origin
    );
  } catch {
    return false;
  }
};
export const rememberLoginReturn = (): void => {
  try {
    const path = window.location.pathname + window.location.search;
    if (sameOriginPath(path)) {
      sessionStorage.setItem(key, path);
    }
  } catch {
    // Storage denial does not prevent authentication.
  }
};
export const restoreLoginReturn = (): void => {
  try {
    const path = sessionStorage.getItem(key);
    sessionStorage.removeItem(key);
    if (path !== null && sameOriginPath(path)) {
      navigate(path, true);
    }
  } catch {
    // The established session still works when storage is unavailable.
  }
};
