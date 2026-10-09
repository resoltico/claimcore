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
  pending: { current: boolean; changed?: () => void },
  action: () => Promise<void>,
): Promise<void> => {
  if (pending.current) {
    return Promise.resolve();
  }
  pending.current = true;
  pending.changed?.();
  return new Promise<void>((resolve) => {
    resolve(action());
  }).finally(() => {
    pending.current = false;
    pending.changed?.();
  });
};

export const createRecoveryAdmission = () => {
  const listeners = new Set<() => void>();
  const pending = {
    current: false,
    changed: () => {
      listeners.forEach((listener) => {
        listener();
      });
    },
  };
  return {
    idle: () => !pending.current,
    getSnapshot: () => pending.current,
    subscribe: (listener: () => void) => {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
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
