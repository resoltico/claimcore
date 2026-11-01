import {
  createContext,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { useCredential, useSessionContext } from "../session/SessionContext";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { useRecoveryJournal } from "./JournalContext";
import { subscribeRecovery } from "./indexedJournal";
import {
  sameRecoveryScope,
  type RecoveryRecord,
  type RecoveryJournal,
  type RecoveryScope,
} from "../protocol/recoveryJournal";
import { service, resultNotice } from "../api/service";
import type { Notice } from "../protocol/notices";

type RecoveryOwner = { scope: RecoveryScope; credential: string; epoch: number };
type RecoveryView = {
  owner: RecoveryOwner | null;
  operations: ReadonlyArray<RecoveryRecord>;
  failed: boolean;
};
type RecoveryAttempt = { owner: RecoveryOwner; attempt: string };
type RecoveryMethod = "CHECK" | "SETTLE";
type Feedback = { owner: RecoveryOwner; notice: Notice; cleanupFailed: boolean };
const finalResolution = (
  result: Awaited<ReturnType<typeof service.resolve>> | Awaited<ReturnType<typeof service.status>>,
) =>
  result.kind === "outcome" &&
  (result.value.outcome.kind === "ACCEPTED" || result.value.outcome.kind === "NOT_ACCEPTED");
const recoverAttempt = async (
  record: RecoveryRecord,
  owner: RecoveryOwner,
  journal: RecoveryJournal,
  current: () => boolean,
  method: RecoveryMethod,
): Promise<{ notice: Notice; removed: boolean; cleanupFailed: boolean } | null> => {
  const result =
    method === "CHECK"
      ? await service.status(record.operationId, owner.credential, record.installationId)
      : await service.resolve(record.operationId, owner.credential, record.installationId);
  if (!current()) {
    return null;
  }
  if (!finalResolution(result) || result.kind !== "outcome") {
    return { notice: resultNotice(result), removed: false, cleanupFailed: false };
  }
  const notice: Notice =
    result.value.outcome.kind === "ACCEPTED"
      ? { kind: "accepted", operationId: record.operationId }
      : { kind: "local", reason: "notAccepted" };
  try {
    await journal.remove(record);
  } catch {
    return { notice, removed: false, cleanupFailed: true };
  }
  if (!current()) {
    return null;
  }
  return {
    removed: true,
    notice,
    cleanupFailed: false,
  };
};
const visibleRecovery = (
  owner: RecoveryOwner | null,
  view: RecoveryView,
  busy: RecoveryAttempt | null,
  feedback: Feedback | null,
) => {
  if (owner === null) {
    return { operations: [], busy: null, notice: null, cleanup: null };
  }
  const unavailable: Notice | null =
    view.owner === owner && view.failed
      ? { kind: "local", reason: "recoveryListingUnavailable" }
      : null;
  return {
    operations:
      view.owner === owner
        ? view.operations.filter((record) => sameRecoveryScope(owner.scope, record))
        : [],
    busy: busy?.owner === owner ? busy.attempt : null,
    notice: feedback?.owner === owner ? feedback.notice : unavailable,
    cleanup:
      feedback?.owner === owner && feedback.cleanupFailed
        ? { kind: "local" as const, reason: "recoveryCleanupUnavailable" as const }
        : null,
  };
};
/** Restart recovery sends only identity. It never reconstructs or resubmits business requests. */
const useRecoveryNotice = () => {
  const credential = useCredential();
  const { epoch } = useSessionContext().state;
  const alive = useRef(true);
  useLayoutEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);
  const requester = useRequesterContext();
  const journal = useRecoveryJournal();
  const actorId = requester?.actorId;
  const installationId = requester?.installationId;
  const scope = useMemo<RecoveryScope | null>(
    () =>
      actorId === undefined || installationId === undefined
        ? null
        : {
            serviceOrigin: location.origin,
            installationId,
            actorId,
          },
    [actorId, installationId],
  );
  // Value-equivalent refreshes keep ownership; every real transition gets a new identity,
  // including A → B → A and requester → null → requester.
  const owner = useMemo<RecoveryOwner | null>(
    () => (scope === null ? null : { scope, credential, epoch }),
    [scope, credential, epoch],
  );
  const latest = useRef(owner);
  useLayoutEffect(() => {
    latest.current = owner;
  }, [owner]);
  const [view, setView] = useState<RecoveryView>({ owner: null, operations: [], failed: false });
  const [busy, setBusy] = useState<RecoveryAttempt | null>(null);
  const [feedback, setFeedback] = useState<Feedback | null>(null);
  useEffect(() => {
    let active = true;
    let serial = 0;
    if (owner === null) {
      return;
    }
    const currentOwner = owner;
    const refresh = () => {
      const request = ++serial;
      void Promise.resolve()
        .then(() => journal.list(currentOwner.scope))
        .then((operations) => {
          if (active && request === serial) {
            setView({ owner: currentOwner, operations, failed: false });
          }
        })
        .catch(() => {
          if (active && request === serial) {
            setView({ owner: currentOwner, operations: [], failed: true });
          }
        });
    };
    refresh();
    const unsubscribe = subscribeRecovery(refresh);
    return () => {
      active = false;
      unsubscribe();
    };
  }, [owner, journal]);
  const perform = async (
    record: RecoveryRecord,
    method: RecoveryMethod,
    expectedOwner: RecoveryOwner | null,
  ) => {
    const current = () => alive.current && owner !== null && latest.current === owner;
    if (
      owner === null ||
      expectedOwner !== owner ||
      !current() ||
      busy?.owner === owner ||
      !sameRecoveryScope(owner.scope, record)
    ) {
      return;
    }
    const attempt: RecoveryAttempt = { owner, attempt: record.attemptId };
    setBusy(attempt);
    try {
      const settled = await recoverAttempt(record, owner, journal, current, method);
      if (settled !== null && current()) {
        if (settled.removed) {
          setView((previous) =>
            previous.owner === owner
              ? {
                  ...previous,
                  operations: previous.operations.filter(
                    (value) => value.attemptId !== record.attemptId,
                  ),
                }
              : previous,
          );
        }
        setFeedback({ owner, notice: settled.notice, cleanupFailed: settled.cleanupFailed });
      }
    } catch {
      if (current()) {
        setFeedback({
          owner,
          notice: { kind: "local", reason: "unreachable" },
          cleanupFailed: false,
        });
      }
    } finally {
      // An obsolete completion must never release a newer attempt's lock.
      setBusy((previous) => (previous === attempt ? null : previous));
    }
  };
  return {
    ...visibleRecovery(owner, view, busy, feedback),
    owner,
    check: (record: RecoveryRecord) => perform(record, "CHECK", owner),
    settle: (record: RecoveryRecord, expectedOwner: RecoveryOwner) =>
      perform(record, "SETTLE", expectedOwner),
  };
};

const RecoveryContext = createContext<ReturnType<typeof useRecoveryNotice> | null>(null);
/** One authenticated workspace owns asynchronous recovery across route and loading boundaries. */
export const RecoveryProvider = ({ children }: { children: ReactNode }) => {
  const recovery = useRecoveryNotice();
  return <RecoveryContext value={recovery}>{children}</RecoveryContext>;
};
export const useRecovery = () => {
  const recovery = useContext(RecoveryContext);
  if (recovery === null) {
    throw new Error("Recovery requires its workspace owner.");
  }
  return recovery;
};
