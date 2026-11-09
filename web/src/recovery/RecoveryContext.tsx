import type { Screen, ReceiptSubject } from "@contracts/client";
import {
  createContext,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from "react";
import { useCredential, useSessionContext } from "../session/SessionContext";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { useEndpointCache } from "../hooks/EndpointProvider";
import { useRecoveryJournal } from "./JournalContext";
import { subscribeRecovery } from "./indexedJournal";
import { sameRecoveryScope, type RecoveryScope } from "../protocol/recoveryJournal";
import { service, resultNotice } from "../api/service";
import type { RecoveryRecord } from "../protocol/recoveryJournal";
import type { Notice } from "../protocol/notices";

type RecoveryOwner = { scope: RecoveryScope; credential: string; epoch: number };
export type RecoveryFinalAnswer = {
  generation: number;
  record: RecoveryRecord;
  outcome:
    | { kind: "RECORDED"; view: Screen; timeZone: string; caseId: string | null }
    | { kind: "NOT_RECORDED"; view: Screen | null };
  cleanup: Notice | null;
};
type RecoveryView = {
  owner: RecoveryOwner | null;
  operations: ReadonlyArray<RecoveryRecord>;
  finals: Readonly<Record<string, RecoveryFinalAnswer>>;
  presentations: Readonly<Record<string, Screen>>;
  busy: string | null;
  notice: Notice | null;
};
const empty: RecoveryView = {
  owner: null,
  operations: [],
  finals: {},
  presentations: {},
  busy: null,
  notice: null,
};

type UpdateRecovery = (change: (previous: RecoveryView) => RecoveryView) => void;
type AttemptFeedback =
  | { kind: "pending"; view: Screen }
  | { kind: "notice"; notice: Notice }
  | { kind: "final"; outcome: RecoveryFinalAnswer["outcome"] };

const disclosedCase = (subject: ReceiptSubject): string | null => {
  switch (subject.kind) {
    case "AUTHORITY":
      return null;
    case "CASE":
    case "WORK":
    case "NOTE":
      return subject.snapshot.kind === "DISCLOSED" ? subject.caseId : null;
    case "REMINDER": {
      const versions =
        subject.effects.kind === "SINGLE"
          ? [subject.effects.reminder]
          : [subject.effects.completed, subject.effects.next];
      return versions.some((version) => version.snapshot.kind === "DISCLOSED")
        ? subject.caseId
        : null;
    }
  }
};

const attemptFeedback = (
  result: Awaited<ReturnType<typeof service.operationGet>>,
): AttemptFeedback => {
  if (result.kind !== "outcome") {
    return { kind: "notice", notice: resultNotice(result) };
  }
  const { outcome } = result.value;
  if (outcome.kind === "PENDING") {
    return { kind: "pending", view: outcome.view };
  }
  if (outcome.kind === "ACCEPTED") {
    const { byRequester, receipt } = outcome.data;
    return {
      kind: "final",
      outcome: byRequester
        ? {
            kind: "RECORDED",
            view: {
              regions: outcome.view.regions.filter((region) =>
                ["IDENTITY", "NOTICE", "DETAILS"].includes(region.role),
              ),
            },
            timeZone: receipt.basis.timeZone,
            caseId: disclosedCase(receipt.subject),
          }
        : { kind: "NOT_RECORDED", view: null },
    };
  }
  if (outcome.kind === "NOT_ACCEPTED") {
    return { kind: "final", outcome: { kind: "NOT_RECORDED", view: outcome.view } };
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
  readGeneration: () => number,
) => {
  const generation = readGeneration();
  const result = await service.operationGet(
    record.operationId,
    owner.credential,
    record.installationId,
    signal,
  );
  if (!current()) {
    return;
  }
  const feedback = attemptFeedback(result);
  if (feedback.kind === "pending") {
    update((previous) => ({
      ...previous,
      notice: null,
      finals: Object.fromEntries(
        Object.entries(previous.finals).filter(([id]) => id !== record.attemptId),
      ),
      presentations: { ...previous.presentations, [record.attemptId]: feedback.view },
    }));
    return;
  }
  if (feedback.kind === "notice") {
    update((previous) => ({
      ...previous,
      notice: feedback.notice,
      finals: Object.fromEntries(
        Object.entries(previous.finals).filter(([id]) => id !== record.attemptId),
      ),
      presentations: Object.fromEntries(
        Object.entries(previous.presentations).filter(([id]) => id !== record.attemptId),
      ),
    }));
    return;
  }
  const final: RecoveryFinalAnswer = {
    record,
    generation,
    outcome: feedback.outcome,
    cleanup: null,
  };
  const show = (previous: RecoveryView, answer: RecoveryFinalAnswer): RecoveryView => {
    if (answer.generation !== readGeneration()) {
      return previous;
    }
    return {
      ...previous,
      notice: null,
      finals: { ...previous.finals, [record.attemptId]: answer },
      presentations: Object.fromEntries(
        Object.entries(previous.presentations).filter(([id]) => id !== record.attemptId),
      ),
    };
  };
  update((previous) => show(previous, final));
  try {
    await journal.remove(record);
    update((previous) => ({
      ...show(previous, final),
      operations: previous.operations.filter((item) => item.attemptId !== record.attemptId),
    }));
  } catch {
    update((previous) =>
      show(previous, {
        ...final,
        cleanup: { kind: "local", reason: "recoveryCleanupUnavailable" },
      }),
    );
  }
};

/** Repeated identity-only reads become final on the service; recovery never resubmits a change. */
const useRecoveryNotice = () => {
  const cache = useEndpointCache();
  if (cache === null) {
    throw new Error("Recovery requires its endpoint cache.");
  }
  const generation = useSyncExternalStore(
    cache.subscribeInvalidation,
    cache.invalidation,
    cache.invalidation,
  );
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
  useEffect(
    () =>
      cache.subscribeInvalidation(() => {
        const currentGeneration = cache.invalidation();
        setView((previous) => ({
          ...previous,
          finals: Object.fromEntries(
            Object.entries(previous.finals).filter(
              ([, answer]) => answer.generation === currentGeneration,
            ),
          ),
        }));
      }),
    [cache],
  );
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
          await checkAttempt(
            record,
            owner,
            journal,
            controller.signal,
            current,
            update,
            cache.invalidation,
          );
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
  }, [owner, journal, cache]);
  const visible = view.owner === owner && owner !== null ? view : empty;
  return {
    ...visible,
    // Invalidation hides disclosed results synchronously without aborting an identity-only check.
    finals: Object.fromEntries(
      Object.entries(visible.finals).filter(([, answer]) => answer.generation === generation),
    ),
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
