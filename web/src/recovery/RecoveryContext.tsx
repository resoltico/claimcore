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
import { sameRecoveryScope, type RecoveryScope } from "../protocol/recoveryJournal";
import { service, resultNotice } from "../api/service";
import type { RecoveryRecord } from "../protocol/recoveryJournal";
import type { Notice } from "../protocol/notices";

type RecoveryOwner = { scope: RecoveryScope; credential: string; epoch: number };
type RecoveryView = {
  owner: RecoveryOwner | null;
  operations: ReadonlyArray<RecoveryRecord>;
  deadlines: Readonly<Record<string, string>>;
  busy: string | null;
  notice: Notice | null;
  cleanup: Notice | null;
};
const empty: RecoveryView = {
  owner: null,
  operations: [],
  deadlines: {},
  busy: null,
  notice: null,
  cleanup: null,
};

type UpdateRecovery = (change: (previous: RecoveryView) => RecoveryView) => void;
type AttemptFeedback =
  | { kind: "pending"; finalAt: string }
  | { kind: "notice"; notice: Notice }
  | { kind: "final"; notice: Notice };

const attemptFeedback = (
  record: RecoveryRecord,
  result: Awaited<ReturnType<typeof service.operationGet>>,
): AttemptFeedback => {
  if (result.kind !== "outcome") {
    return { kind: "notice", notice: resultNotice(result) };
  }
  const { outcome } = result.value;
  if (outcome.kind === "PENDING") {
    return { kind: "pending", finalAt: outcome.data.finalAt };
  }
  if (outcome.kind === "ACCEPTED") {
    const { byRequester } = outcome.data;
    return {
      kind: "final",
      notice: byRequester
        ? { kind: "accepted", operationId: record.operationId }
        : { kind: "local", reason: "notAccepted" },
    };
  }
  if (outcome.kind === "NOT_ACCEPTED") {
    return { kind: "final", notice: { kind: "local", reason: "notAccepted" } };
  }
  return { kind: "notice", notice: resultNotice(result) };
};

const checkAttempt = async (
  record: RecoveryRecord,
  owner: RecoveryOwner,
  journal: ReturnType<typeof useRecoveryJournal>,
  signal: AbortSignal,
  current: () => boolean,
  update: UpdateRecovery,
) => {
  const result = await service.operationGet(
    record.operationId,
    owner.credential,
    record.installationId,
    signal,
  );
  if (!current()) {
    return;
  }
  const feedback = attemptFeedback(record, result);
  if (feedback.kind === "pending") {
    update((previous) => ({
      ...previous,
      deadlines: { ...previous.deadlines, [record.attemptId]: feedback.finalAt },
    }));
    return;
  }
  if (feedback.kind === "notice") {
    update((previous) => ({ ...previous, notice: feedback.notice }));
    return;
  }
  try {
    await journal.remove(record);
    update((previous) => ({
      ...previous,
      operations: previous.operations.filter((item) => item.attemptId !== record.attemptId),
      notice: feedback.notice,
      cleanup: null,
    }));
  } catch {
    update((previous) => ({
      ...previous,
      notice: feedback.notice,
      cleanup: { kind: "local", reason: "recoveryCleanupUnavailable" },
    }));
  }
};

/** Repeated identity-only reads become final on the service; recovery never resubmits a change. */
const useRecoveryNotice = () => {
  const credential = useCredential();
  const { epoch } = useSessionContext().state;
  const requester = useRequesterContext();
  const actorId = requester?.actorId;
  const installationId = requester?.installationId;
  const journal = useRecoveryJournal();
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
  const owner = useMemo<RecoveryOwner | null>(
    () => (scope === null ? null : { scope, credential, epoch }),
    [scope, credential, epoch],
  );
  const latest = useRef(owner);
  useLayoutEffect(() => {
    latest.current = owner;
  }, [owner]);
  const [view, setView] = useState<RecoveryView>(empty);
  const refresh = useRef<() => void>(() => undefined);
  useEffect(() => {
    if (owner === null) {
      return;
    }
    let active = true;
    let running = false;
    const controller = new AbortController();
    const current = () => active && latest.current === owner;
    const update = (change: (previous: RecoveryView) => RecoveryView) => {
      if (current()) {
        setView((previous) => change(previous.owner === owner ? previous : { ...empty, owner }));
      }
    };
    const cycle = async () => {
      if (running || !current()) {
        return;
      }
      running = true;
      try {
        const operations = (await journal.list(owner.scope)).filter((record) =>
          sameRecoveryScope(owner.scope, record),
        );
        if (!current()) {
          return;
        }
        update((previous) => ({ ...previous, operations }));
        for (const record of operations) {
          if (!current()) {
            return;
          }
          update((previous) => ({ ...previous, busy: record.attemptId }));
          // oxlint-disable-next-line no-await-in-loop -- One scoped attempt at a time avoids background bursts and stops on an owner change.
          await checkAttempt(record, owner, journal, controller.signal, current, update);
        }
      } catch {
        update((previous) => ({
          ...previous,
          notice: { kind: "local", reason: "recoveryListingUnavailable" },
        }));
      } finally {
        running = false;
        update((previous) => ({ ...previous, busy: null }));
      }
    };
    const run = () => {
      void cycle();
    };
    refresh.current = run;
    run();
    const timer = window.setInterval(run, 1000);
    const unsubscribe = subscribeRecovery(run);
    return () => {
      active = false;
      controller.abort();
      window.clearInterval(timer);
      unsubscribe();
      if (refresh.current === run) {
        refresh.current = () => undefined;
      }
    };
  }, [owner, journal]);
  return {
    ...(view.owner === owner && owner !== null ? view : empty),
    owner,
    check: (_record: RecoveryRecord) => {
      refresh.current();
    },
  };
};

const RecoveryContext = createContext<ReturnType<typeof useRecoveryNotice> | null>(null);
/** One authenticated workspace owns recovery across route and loading boundaries. */
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
