const signedOutKey = "claimcore.signedOut";
const attemptsKey = "claimcore.automaticSignIn";
const windowMilliseconds = 60_000;
const maximumAttempts = 2;

const tabStorage = () => window.sessionStorage;

export const signInBookkeeping = (
  getStorage = tabStorage,
): "signedOut" | "ready" | "unavailable" => {
  try {
    return getStorage().getItem(signedOutKey) === "true" ? "signedOut" : "ready";
  } catch {
    return "unavailable";
  }
};

export const markSignedOut = (getStorage = tabStorage): void => {
  try {
    const storage = getStorage();
    storage.setItem(signedOutKey, "true");
    storage.removeItem(attemptsKey);
  } catch {
    // Storage may be unavailable. Automatic sign-in also requires storage.
  }
};

export const resetSignIn = (getStorage = tabStorage): void => {
  try {
    const storage = getStorage();
    storage.removeItem(signedOutKey);
    storage.removeItem(attemptsKey);
  } catch {
    // Explicit sign-in does not depend on storage.
  }
};

export const recordAutomaticSignIn = (now: number, getStorage = tabStorage): boolean => {
  try {
    const storage = getStorage();
    if (storage.getItem(signedOutKey) === "true") {
      return false;
    }
    const previous = (storage.getItem(attemptsKey) ?? "")
      .split(",")
      .filter((value) => value !== "")
      .map(Number)
      .filter((value) => value <= now && now - value < windowMilliseconds);
    if (previous.length >= maximumAttempts) {
      return false;
    }
    storage.setItem(attemptsKey, [...previous, now].join(","));
    return true;
  } catch {
    return false;
  }
};
