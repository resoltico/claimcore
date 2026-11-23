const storageKey = "claimcore.lastWorklist";

export const rememberWorklist = (path: string): void => {
  try {
    window.sessionStorage.setItem(storageKey, path);
  } catch {
    // Navigation remains available without storage.
  }
};

export const lastWorklist = (): string => {
  try {
    const path = window.sessionStorage.getItem(storageKey);
    if (path !== null) {
      const target = new URL(path, window.location.origin);
      if (target.origin === window.location.origin && target.pathname === "/cases") {
        return target.pathname + target.search;
      }
    }
  } catch {
    // The installed worklist is the fallback when navigation history was unavailable.
  }
  return "/cases";
};
