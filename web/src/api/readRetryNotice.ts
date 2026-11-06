type ReadObserver = { credential: string; changed: (waiting: boolean) => void; waiting: number };
let current: ReadObserver | null = null;
/** A fresh registration is a new authenticated/requester generation, even for the same token. */
export const subscribeReadRetries = (credential: string, changed: (waiting: boolean) => void) => {
  const owner = { credential, changed, waiting: 0 };
  current = owner;
  return () => {
    if (current === owner) {
      current = null;
    }
  };
};
const noNotice = () => {
  /* This dispatch has no current workspace observer. */
};
/** Capture at dispatch so an old retry cannot publish into a replacement workspace. */
export const captureReadRetryNotice = (credential: string | undefined) => {
  const owner = current?.credential === credential ? current : null;
  return () => {
    if (owner === null || current !== owner) {
      return noNotice;
    }
    owner.waiting += 1;
    owner.changed(true);
    let waiting = true;
    return () => {
      if (!waiting) {
        return;
      }
      waiting = false;
      if (current === owner) {
        owner.waiting -= 1;
        owner.changed(owner.waiting > 0);
      }
    };
  };
};
