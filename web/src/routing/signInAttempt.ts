const signedOutKey = "claimcore.signedOut";
const attemptsKey = "claimcore.automaticSignIn";
const windowMilliseconds = 60_000;
const maximumAttempts = 2;

export const signedOut = (storage: Storage): boolean => {
  try {
    return storage.getItem(signedOutKey) === "true";
  } catch {
    return false;
  }
};

export const markSignedOut = (storage: Storage): void => {
  try {
    storage.setItem(signedOutKey, "true");
    storage.removeItem(attemptsKey);
  } catch {
    // Storage may be unavailable. Automatic sign-in also requires storage.
  }
};

export const resetSignIn = (storage: Storage): void => {
  try {
    storage.removeItem(signedOutKey);
    storage.removeItem(attemptsKey);
  } catch {
    // Explicit sign-in does not depend on storage.
  }
};

export const recordAutomaticSignIn = (storage: Storage, now: number): boolean => {
  try {
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
