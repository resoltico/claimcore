export type InspectionControl = {
  begin: () => AbortSignal;
  cancel: () => void;
  complete: () => void;
  idle: () => boolean;
};
export const createInspectionControl = (): InspectionControl => {
  let current: AbortController | null = null;
  return {
    begin: () => {
      current?.abort();
      current = new AbortController();
      return current.signal;
    },
    cancel: () => {
      current?.abort();
      current = null;
    },
    complete: () => {
      current = null;
    },
    idle: () => current === null,
  };
};

export const onceWhilePending = (
  pending: { current: boolean },
  action: () => Promise<void>,
): Promise<void> => {
  if (pending.current) {
    return Promise.resolve();
  }
  pending.current = true;
  return action().finally(() => {
    pending.current = false;
  });
};

export const createRecoveryAdmission = () => {
  const pending = { current: false };
  return {
    idle: () => !pending.current,
    read: (action: () => Promise<void>) => onceWhilePending(pending, action),
    mutate: (action: () => Promise<void>, onLockChange: (locked: boolean) => void) =>
      onceWhilePending(pending, async () => {
        onLockChange(true);
        try {
          await action();
        } finally {
          onLockChange(false);
        }
      }),
  };
};
