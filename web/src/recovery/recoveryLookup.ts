import { service } from "../api/service";
import type { RecoveryRecord, RecoveryScope } from "../protocol/recoveryJournal";

export type RecoveryReply = Awaited<ReturnType<typeof service.operationGet>>;

type Schedule = {
  next: number;
  failures: number;
  active: {
    credential: string;
    epoch: number;
    users: number;
    request: AbortController;
    reply: Promise<RecoveryReply>;
  } | null;
};

const schedules = new Map<string, Schedule>();
const cancelled = {
  kind: "deliveryFailure",
  notice: { kind: "local", reason: "cancelled" },
} as const satisfies RecoveryReply;

const coordinationUnavailable = {
  kind: "deliveryFailure",
  notice: { kind: "local", reason: "recoveryCoordinationUnavailable" },
} as const satisfies RecoveryReply;

/** The scoped coordination state already presents storage failures to the workspace. */
export const lookupHasFeedback = (reply: RecoveryReply): boolean =>
  reply !== cancelled && reply !== coordinationUnavailable;

const waitUntil = (next: number, signal: AbortSignal): Promise<void> =>
  new Promise((resolve) => {
    if (signal.aborted || next <= Date.now()) {
      resolve();
      return;
    }
    const timer = setTimeout(() => {
      signal.removeEventListener("abort", abort);
      resolve();
    }, next - Date.now());
    function abort() {
      clearTimeout(timer);
      resolve();
    }
    signal.addEventListener("abort", abort, { once: true });
  });

const nextDelay = (reply: RecoveryReply, failures: number, now: number): number => {
  const delay = Math.min(30_000, 1000 * 2 ** Math.min(failures, 5));
  const jitter = Math.round(delay * (0.85 + Math.random() * 0.15));
  if (reply.kind === "outcome" && reply.value.outcome.kind === "PENDING") {
    const remaining = Date.parse(reply.value.outcome.finalAt) - now;
    return remaining > 0 ? Math.min(jitter, remaining) : jitter;
  }
  return reply.kind === "hostFailure" ? Math.max(jitter, reply.retryAfterMs ?? 0) : jitter;
};

const scopeKey = (scope: RecoveryScope): string =>
  JSON.stringify([scope.serviceOrigin, scope.installationId, scope.actorId]);

export type RecoveryLeadership = { readonly signal: AbortSignal; readonly current: () => boolean };

const leadershipMilliseconds = 2500;
const heartbeatMilliseconds = 500;
const readLeaseMilliseconds = 3000;

const readLeadership = (key: string): { owner: string; until: number } | null => {
  const stored = localStorage.getItem(key);
  if (stored === null) {
    return null;
  }
  const [owner, expires] = stored.split(":");
  const until = Number(expires);
  const now = Date.now();
  return owner !== undefined &&
    Number.isSafeInteger(until) &&
    until > now &&
    until <= now + leadershipMilliseconds
    ? { owner, until }
    : null;
};

// The cell contains only scheduling metadata and a per-read fence. Each tab
// still obtains the operation result using its own authorized session.
type ReadCell = { next: number; failures: number; owner: string; until: number };
const emptyCell: ReadCell = { next: 0, failures: 0, owner: "", until: 0 };

const readCell = (name: string): ReadCell => {
  const stored = localStorage.getItem(name);
  const parts = stored?.match(/^(\d+):(\d+):([0-9a-f-]*):(\d+)$/u);
  if (parts === undefined || parts === null) {
    return emptyCell;
  }
  const [, deadline, count, owner = "", expiry] = parts;
  const now = Date.now();
  const next = Number(deadline);
  const until = Number(expiry);
  return {
    next: next > now && next <= now + 300_000 ? next : 0,
    failures: Math.min(6, Number(count)),
    owner,
    until: until > now && until <= now + readLeaseMilliseconds ? until : 0,
  };
};

const writeCell = (name: string, cell: ReadCell) => {
  if (cell.next === 0 && cell.until === 0) {
    localStorage.removeItem(name);
  } else {
    localStorage.setItem(name, `${cell.next}:${cell.failures}:${cell.owner}:${cell.until}`);
  }
};

const readLock = (key: string): string => `claimcore-recovery:${key}`;
const cellName = (key: string): string => `claimcore-recovery-schedule:${key}`;
const storageFault = Symbol("recovery storage fault");
const readStorageFaults = new Map<string, Set<string>>();

const coordinateRead = async <Value>(
  locks: LockManager,
  scope: string,
  key: string,
  work: () => Value,
): Promise<Value | typeof storageFault> => {
  try {
    return await locks.request(readLock(key), work);
  } catch {
    const failed = readStorageFaults.get(scope) ?? new Set<string>();
    failed.add(key);
    readStorageFaults.set(scope, failed);
    return storageFault;
  }
};

/** Exercise acquisition, renewal and clearing on the affected cell without sending a read.
 * Leave another tab's permit and any shared backoff intact. */
const checkReadStorage = async (scope: string, locks: LockManager) => {
  const failed = readStorageFaults.get(scope);
  const key = failed?.values().next().value;
  if (failed === undefined || key === undefined) {
    return;
  }
  const name = cellName(key);
  const checked = await coordinateRead(locks, scope, key, () => {
    const cell = readCell(name);
    if (cell.next > 0 || cell.until > 0) {
      return false;
    }
    // A partly written probe must not keep another tab from acquiring a permit.
    const probe = { ...cell, owner: crypto.randomUUID(), until: Date.now() - 1 };
    writeCell(name, probe);
    writeCell(name, { ...probe, until: probe.until - 1 });
    localStorage.removeItem(name);
    return true;
  });
  if (checked === true) {
    failed.delete(key);
    if (failed.size === 0) {
      readStorageFaults.delete(scope);
    }
  }
};

/** The lock protects only a synchronous lease update. Frozen pages cannot retain it. */
export const leadRecovery = (
  scope: RecoveryScope,
  signal: AbortSignal,
  changed: (leadership: RecoveryLeadership | null) => void,
  coordinationChanged: (unavailable: boolean) => void = () => undefined,
): void => {
  const { locks } = navigator;
  if (locks === undefined) {
    changed({ signal, current: () => !signal.aborted });
    return;
  }
  if (signal.aborted) {
    return;
  }

  const key = `claimcore-recovery-leader:${scopeKey(scope)}`;
  let owner = "";
  let suspended = false;
  let pending = false;
  let coordinationFailed = false;
  const scopeId = scopeKey(scope);
  let active: { owner: string; controller: AbortController } | null = null;

  const eligible = () => !signal.aborted && !suspended && document.visibilityState === "visible";
  const ownsLease = (token: string) => {
    try {
      return eligible() && readLeadership(key)?.owner === token;
    } catch {
      return false;
    }
  };
  const demote = () => {
    if (active !== null) {
      active.controller.abort();
      active = null;
      changed(null);
    }
  };
  const elect = () => {
    if (active?.owner === owner) {
      return;
    }
    demote();
    const token = owner;
    const controller = new AbortController();
    active = { owner: token, controller };
    changed({
      signal: controller.signal,
      current: () => !controller.signal.aborted && ownsLease(token),
    });
  };
  const update = () => {
    const previous = readLeadership(key);
    if (!eligible()) {
      if (previous?.owner === owner) {
        localStorage.removeItem(key);
      }
      return;
    }
    if (previous === null) {
      owner = crypto.randomUUID();
    }
    if (previous === null || previous.owner === owner) {
      localStorage.setItem(key, `${owner}:${Date.now() + leadershipMilliseconds}`);
    }
  };
  const reportCoordination = (failed: boolean) => {
    if (coordinationFailed !== failed) {
      coordinationFailed = failed;
      coordinationChanged(failed);
    }
  };
  const tick = () => {
    if (!eligible()) {
      demote();
    }
    if (pending) {
      return;
    }
    pending = true;
    void locks
      .request(key, { ifAvailable: true }, (lock) => {
        if (lock !== null) {
          update();
          return true;
        }
        return false;
      })
      .then(async (updated) => {
        if (eligible()) {
          await checkReadStorage(scopeId, locks);
        }
        if (updated && eligible()) {
          reportCoordination(readStorageFaults.has(scopeId));
        }
        if (ownsLease(owner)) {
          elect();
        } else {
          demote();
        }
      })
      .catch(() => {
        demote();
        if (eligible()) {
          reportCoordination(true);
        }
      })
      .finally(() => {
        pending = false;
      });
  };
  const pause = () => {
    suspended = true;
    tick();
  };
  const resume = () => {
    suspended = false;
    tick();
  };
  const timer = window.setInterval(tick, heartbeatMilliseconds);
  document.addEventListener("visibilitychange", tick);
  document.addEventListener("freeze", pause);
  document.addEventListener("resume", resume);
  window.addEventListener("pagehide", pause);
  window.addEventListener("pageshow", resume);
  signal.addEventListener(
    "abort",
    () => {
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", tick);
      document.removeEventListener("freeze", pause);
      document.removeEventListener("resume", resume);
      window.removeEventListener("pagehide", pause);
      window.removeEventListener("pageshow", resume);
      tick();
    },
    { once: true },
  );
  tick();
};

type ReadResult = RecoveryReply | typeof storageFault;
type ReadLoss = typeof cancelled | typeof storageFault;

const lookupWithPermit = async (
  locks: LockManager,
  scope: string,
  key: string,
  owner: string,
  current: () => boolean,
  transport: (signal: AbortSignal) => Promise<RecoveryReply>,
  settle: (cell: ReadCell, reply: RecoveryReply) => void,
): Promise<ReadResult> => {
  const name = cellName(key);
  const fenced = <Value>(change: (cell: ReadCell) => Value) =>
    coordinateRead(locks, scope, key, () => {
      const cell = readCell(name);
      return cell.owner === owner && cell.until > 0 ? change(cell) : null;
    });
  const abortTransport = new AbortController();
  const pending: { loss: ReadLoss | null; renewal: Promise<void> | null } = {
    loss: null,
    renewal: null,
  };
  const abandoned = new Promise<ReadLoss>((resolve) => {
    abortTransport.signal.addEventListener(
      "abort",
      () => {
        resolve(pending.loss ?? cancelled);
      },
      { once: true },
    );
  });
  const renew = () => {
    if (pending.renewal !== null) {
      return;
    }
    pending.renewal = fenced((cell) => {
      if (!current()) {
        return false;
      }
      writeCell(name, { ...cell, until: Date.now() + readLeaseMilliseconds });
      return true;
    }).then((result) => {
      if (result !== true) {
        pending.loss = result === storageFault ? storageFault : cancelled;
        abortTransport.abort();
      }
      pending.renewal = null;
    });
  };

  const timer = setInterval(renew, heartbeatMilliseconds);
  const reply = await Promise.race([transport(abortTransport.signal), abandoned])
    .catch(() => cancelled)
    .finally(() => {
      clearInterval(timer);
    });
  if (pending.renewal !== null) {
    await pending.renewal;
  }
  const outcome = pending.loss ?? reply;
  if (outcome === storageFault) {
    // A failed fence expires on its own. A stale read may still be on the wire.
    return storageFault;
  }

  const finished = await fenced((cell) => {
    if (outcome === cancelled || !current()) {
      writeCell(name, { ...cell, owner: "", until: 0 });
      return cancelled;
    }
    settle(cell, outcome);
    return outcome;
  });
  return finished ?? cancelled;
};

type LookupOptions = { admitted?: () => boolean; automatic?: boolean };

const readyTurns = new Map<string, Promise<unknown>>();

const releaseTurn = (work: Promise<unknown>, signal: AbortSignal): Promise<void> =>
  new Promise((resolve) => {
    const release = () => {
      signal.removeEventListener("abort", release);
      resolve();
    };
    if (signal.aborted) {
      release();
    } else {
      signal.addEventListener("abort", release, { once: true });
      void work.then(release, release);
    }
  });

/** Only a ready request occupies the local queue. A cancelled owner releases its turn even
 * if transport settlement lags. */
const inReadyOrder = <Result>(
  scope: string,
  signal: AbortSignal,
  work: () => Promise<Result>,
): Promise<Result> => {
  const previous = readyTurns.get(scope) ?? Promise.resolve();
  const next = previous.then(work);
  const turn = previous.then(() => releaseTurn(next, signal));
  readyTurns.set(scope, turn);
  void turn.then(() => {
    if (readyTurns.get(scope) === turn) {
      readyTurns.delete(scope);
    }
  });
  return next;
};

const start = (
  scope: string,
  key: string,
  schedule: Schedule,
  record: RecoveryRecord,
  credential: string,
  epoch: number,
  admitted: () => boolean,
  automatic: boolean,
) => {
  const request = new AbortController();
  const current = () => !request.signal.aborted && admitted();
  const name = cellName(key);
  const { locks } = navigator;
  const transport = (signal: AbortSignal) =>
    service.operationGet(
      record.operationId,
      credential,
      record.installationId,
      AbortSignal.any([request.signal, signal]),
    );
  const backoff = (reply: RecoveryReply, previousFailures: number) => {
    const terminal =
      reply.kind === "outcome" &&
      (reply.value.outcome.kind === "ACCEPTED" || reply.value.outcome.kind === "NOT_ACCEPTED");
    const now = Date.now();
    return terminal
      ? { next: 0, failures: 0 }
      : {
          next: now + nextDelay(reply, previousFailures, now),
          failures: Math.min(6, previousFailures + 1),
        };
  };
  const localRead = async () => {
    const reply = await transport(request.signal);
    if (!current()) {
      return cancelled;
    }
    Object.assign(schedule, backoff(reply, schedule.failures));
    return reply;
  };
  const read = async (): Promise<RecoveryReply | null> => {
    if (!current()) {
      return cancelled;
    }
    if (locks === undefined) {
      return localRead();
    }
    const failed = readStorageFaults.get(scope);
    if (automatic && failed !== undefined) {
      return coordinationUnavailable;
    }
    if (failed?.has(key) === true) {
      return localRead();
    }
    const owner = crypto.randomUUID();
    const acquire = await coordinateRead(locks, scope, key, () => {
      const cell = readCell(name);
      if (
        !current() ||
        (automatic && readStorageFaults.has(scope)) ||
        cell.next > 0 ||
        cell.until > 0
      ) {
        schedule.next = Math.max(cell.next, Date.now() + 100);
        return null;
      }
      const until = Date.now() + readLeaseMilliseconds;
      writeCell(name, { ...cell, owner, until });
      return until;
    });
    if (acquire === null) {
      return null;
    }
    const result =
      acquire === storageFault
        ? storageFault
        : await lookupWithPermit(
            locks,
            scope,
            key,
            owner,
            current,
            (signal) =>
              current() && Date.now() < acquire ? transport(signal) : Promise.resolve(cancelled),
            (cell, reply) => {
              const delay = backoff(reply, cell.failures);
              writeCell(name, { ...delay, owner: "", until: 0 });
              Object.assign(schedule, delay);
            },
          );
    if (!current()) {
      return cancelled;
    }
    if (result === storageFault) {
      // Only explicit checks can retry as an uncoordinated passive read.
      return automatic ? coordinationUnavailable : localRead();
    }
    return result;
  };
  const ready = async (): Promise<RecoveryReply> => {
    while (current()) {
      // oxlint-disable-next-line no-await-in-loop -- Backoff is per identity and holds no lookup lock.
      await waitUntil(schedule.next, request.signal);
      if (request.signal.aborted) {
        break;
      }
      // oxlint-disable-next-line no-await-in-loop -- A peer may have moved the shared deadline while this request queued.
      const reply = await inReadyOrder(scope, request.signal, read);
      if (reply !== null) {
        return reply;
      }
      schedule.next = Math.max(schedule.next, Date.now() + 100);
    }
    return cancelled;
  };
  const reply = ready();
  const active = { credential, epoch, request, users: 0, reply: reply.catch(() => cancelled) };
  schedule.active = active;
  void active.reply.then(() => {
    if (schedule.active === active) {
      schedule.active = null;
      if (schedule.failures === 0 || request.signal.aborted) {
        schedules.delete(key);
      }
    }
  });
  return active;
};

const subscribe = (
  schedule: Schedule,
  active: NonNullable<Schedule["active"]>,
  signal: AbortSignal,
): Promise<RecoveryReply> => {
  active.users += 1;
  return new Promise<RecoveryReply>((resolve) => {
    let settled = false;
    function finish(reply: RecoveryReply) {
      if (settled) {
        return;
      }
      settled = true;
      signal.removeEventListener("abort", abort);
      active.users -= 1;
      if (active.users === 0 && schedule.active === active && signal.aborted) {
        active.request.abort();
      }
      resolve(reply);
    }
    function abort() {
      finish(cancelled);
    }
    signal.addEventListener("abort", abort, { once: true });
    if (signal.aborted) {
      abort();
    } else {
      void active.reply.then(finish);
    }
  });
};

/** The command owning the UI may disappear while another subscriber is waiting for the same read. */
export const recoveryLookup = (
  record: RecoveryRecord,
  credential: string,
  epoch: number,
  signal: AbortSignal,
  { admitted = () => true, automatic = false }: LookupOptions = {},
): Promise<RecoveryReply> => {
  if (signal.aborted || !admitted()) {
    return Promise.resolve(cancelled);
  }
  const scope = scopeKey(record);
  const key = JSON.stringify([scope, record.operationId]);
  const schedule = schedules.get(key) ?? { next: 0, failures: 0, active: null };
  schedules.set(key, schedule);
  let { active } = schedule;
  if (active !== null && (active.credential !== credential || active.epoch !== epoch)) {
    active.request.abort();
    active = null;
  }
  const selected =
    active === null || active.request.signal.aborted
      ? start(scope, key, schedule, record, credential, epoch, admitted, automatic)
      : active;
  return subscribe(schedule, selected, signal);
};
