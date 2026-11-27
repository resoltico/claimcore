import {
  leadRecovery,
  lookupHasFeedback,
  recoveryLookup,
  type RecoveryReply,
  type RecoveryLeadership,
} from "./recoveryLookup";
import { caseLinkId } from "../routing/coreLinks";
import type { OwnerScope } from "../protocol/ownerScope";
import type { Screen } from "@contracts/client";
import {
  createContext,
  useContext,
  useCallback,
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
import {
  sameRecoveryScope,
  type JournalIssue,
  type RecoveryScope,
  type RecoveryRecord,
} from "../protocol/recoveryJournal";
import { resultNotice, type service } from "../api/service";
import { isDisclosureRefused } from "../api/outcomes";

import type { Notice } from "../protocol/notices";
import type { EndpointCache } from "../hooks/endpointCache";

type RecoveryOwner = OwnerScope & { scope: RecoveryScope; credential: string };
type RecoveryOwnerIdentity = OwnerScope & { credential: string };
type RecoveryObserver = (reply: RecoveryReply, owner: RecoveryOwner) => void;

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
  presentations: Readonly<Record<string, { generation: number; view: Screen }>>;
  busy: string | null;
  notice: Notice | null;
  coordinationUnavailable: boolean;
  journalIssues: ReadonlyArray<JournalIssue | "unavailable">;
};
const unobservedHandoffs = (
  scope: RecoveryScope,
  operations: ReadonlyArray<RecoveryRecord>,
  confirmed: ReadonlyArray<RecoveryRecord>,
  observed: ReadonlySet<string>,
  seen: RecoveryView["finals"],
  generation: number,
) => {
  const active = new Set(operations.map((record) => record.attemptId));
  return confirmed.filter(
    (record) =>
      sameRecoveryScope(scope, record) &&
      !active.has(record.attemptId) &&
      !observed.has(record.attemptId) &&
      seen[record.attemptId]?.generation !== generation,
  );
};
const empty: RecoveryView = {
  owner: null,
  operations: [],
  finals: {},
  presentations: {},
  busy: null,
  notice: null,
  coordinationUnavailable: false,
  journalIssues: [],
};

type UpdateRecovery = (change: (previous: RecoveryView) => RecoveryView) => void;
type AttemptFeedback =
  | { kind: "pending"; view: Screen }
  | { kind: "notice"; notice: Notice }
  | { kind: "final"; outcome: RecoveryFinalAnswer["outcome"] };

const attemptFeedback = (
  result: Awaited<ReturnType<typeof service.operationGet>>,
): AttemptFeedback => {
  if (result.kind !== "outcome") {
    return { kind: "notice", notice: resultNotice(result) };
  }
  const { outcome } = result.value;
  if (outcome.kind === "PENDING") {
    return { kind: "pending", view: outcome.document.screen };
  }
  if (outcome.kind === "ACCEPTED") {
    const { byRequester, document } = outcome;
    if (document.basis.kind !== "SNAPSHOT") {
      return { kind: "notice", notice: { kind: "local", reason: "incomplete" } };
    }
    return {
      kind: "final",
      outcome: byRequester
        ? {
            kind: "RECORDED",
            view: document.screen,
            timeZone: document.basis.observation.timeZone,
            caseId:
              document.links.flatMap((named) =>
                named.rel === "OPEN_CASE" && caseLinkId(named.link) !== undefined
                  ? [caseLinkId(named.link)!]
                  : [],
              )[0] ?? null,
          }
        : { kind: "NOT_RECORDED", view: null },
    };
  }
  if (outcome.kind === "NOT_ACCEPTED") {
    return { kind: "final", outcome: { kind: "NOT_RECORDED", view: outcome.document.screen } };
  }
  return { kind: "notice", notice: resultNotice(result) };
};

const settleFinal = async (
  final: RecoveryFinalAnswer,
  disclose: boolean,
  journal: ReturnType<typeof useRecoveryJournal>,
  update: UpdateRecovery,
  cache: EndpointCache,
) => {
  const show = (previous: RecoveryView, answer: RecoveryFinalAnswer): RecoveryView => {
    if (!disclose || answer.generation !== cache.invalidation()) {
      return previous;
    }
    return {
      ...previous,
      notice: null,
      finals: { ...previous.finals, [final.record.attemptId]: answer },
      presentations: Object.fromEntries(
        Object.entries(previous.presentations).filter(([id]) => id !== final.record.attemptId),
      ),
    };
  };
  update((previous) => show(previous, final));
  try {
    await (journal.confirm?.(final.record) ?? journal.remove(final.record));
    update((previous) => ({
      ...show(previous, final),
      operations: previous.operations.filter((item) => item.attemptId !== final.record.attemptId),
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

const observeReceipt = (result: RecoveryReply, cache: EndpointCache, initial: number) => {
  let generation = initial;
  if (isDisclosureRefused(result)) {
    cache.invalidate();
  }
  const outcome = result.kind === "outcome" ? result.value.outcome : null;
  let disclose = true;
  if (outcome?.kind === "ACCEPTED" && outcome.byRequester) {
    const wasCurrent = generation === cache.invalidation();
    disclose = cache.observeAttachmentReceipt(outcome.document);
    if (wasCurrent) {
      generation = cache.invalidation();
    }
  }
  return { disclose, generation };
};

const checkAttempt = async (
  record: RecoveryRecord,
  owner: RecoveryOwner,
  journal: ReturnType<typeof useRecoveryJournal>,
  signal: AbortSignal,
  current: () => boolean,
  automatic: boolean,
  update: UpdateRecovery,
  cache: EndpointCache,
  observe: RecoveryObserver,
) => {
  const beforeLookup = cache.invalidation();
  const result = await recoveryLookup(record, owner.credential, owner.sessionEpoch, signal, {
    admitted: current,
    automatic,
  });
  if (!current() || !lookupHasFeedback(result)) {
    return false;
  }
  const { disclose, generation } = observeReceipt(result, cache, beforeLookup);
  if (disclose && generation === cache.invalidation()) {
    observe(result, owner);
  }
  const feedback = attemptFeedback(result);
  if (feedback.kind === "pending") {
    update((previous) => ({
      ...previous,
      notice: null,
      finals: Object.fromEntries(
        Object.entries(previous.finals).filter(([id]) => id !== record.attemptId),
      ),
      presentations: {
        ...previous.presentations,
        [record.attemptId]: { generation, view: feedback.view },
      },
    }));
    return false;
  }
  if (feedback.kind === "notice") {
    update((previous) => ({
      ...previous,
      notice: feedback.notice,
      presentations: Object.fromEntries(
        Object.entries(previous.presentations).filter(([id]) => id !== record.attemptId),
      ),
    }));
    return false;
  }
  await settleFinal(
    { record, generation, outcome: feedback.outcome, cleanup: null },
    disclose,
    journal,
    update,
    cache,
  );
  return true;
};

/** Repeated identity-only reads become final on the service; recovery never resubmits a change. */
const verifiedRecoveryIdentity = (
  identity: RecoveryOwnerIdentity | null,
  credential: string,
  epoch: number,
) =>
  identity?.credential === credential &&
  identity.serviceOrigin === location.origin &&
  identity.sessionEpoch === epoch
    ? identity
    : null;
const useRecoveryNotice = (identity: RecoveryOwnerIdentity | null) => {
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
  const identified = verifiedRecoveryIdentity(identity, credential, epoch);
  const actorId = identified?.actorId;
  const installationId = identified?.installationId;
  const verified =
    requester !== null &&
    requester.subject.actorId === actorId &&
    requester.subject.installationId === installationId;
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
    () => (scope === null ? null : { ...scope, scope, credential, sessionEpoch: epoch }),
    [scope, credential, epoch],
  );
  const latest = useRef(owner);
  useLayoutEffect(() => {
    latest.current = owner;
  }, [owner]);
  const [view, setView] = useState<RecoveryView>(empty);
  const viewRef = useRef(view);
  useLayoutEffect(() => {
    viewRef.current = view;
  }, [view]);
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
  const observers = useRef(new Map<string, Set<RecoveryObserver>>());
  const subscribe = useCallback((operationId: string, observer: RecoveryObserver) => {
    const listeners = observers.current.get(operationId) ?? new Set<RecoveryObserver>();
    observers.current.set(operationId, listeners);
    listeners.add(observer);
    return () => {
      listeners.delete(observer);
      if (listeners.size === 0) {
        observers.current.delete(operationId);
      }
    };
  }, []);
  const check = useRef<(record: RecoveryRecord) => void>(() => undefined);
  useEffect(() => {
    if (owner === null) {
      return;
    }
    let leader: RecoveryLeadership | null = null;
    const listing = new Set<string>();
    const issuesByRead = new Map<string, ReadonlySet<JournalIssue | "unavailable">>();
    let pendingRecords: ReadonlyArray<RecoveryRecord> = [];
    let confirmations: ReadonlyArray<RecoveryRecord> = [];
    let coordinationFailed = false;
    let automaticChecks = new AbortController();
    const controller = new AbortController();
    const observed = new Set<string>();
    const scheduled = new Set<string>();
    const current = () => !controller.signal.aborted && latest.current === owner;
    const update = (change: (previous: RecoveryView) => RecoveryView) => {
      if (current()) {
        setView((previous) => change(previous.owner === owner ? previous : { ...empty, owner }));
      }
    };
    const unavailable = () => {
      update((previous) => ({
        ...previous,
        notice: { kind: "local", reason: "recoveryListingUnavailable" },
      }));
    };
    const enqueue = (
      record: RecoveryRecord,
      automatic: boolean,
      leadership?: RecoveryLeadership,
    ) => {
      const signal = automatic
        ? AbortSignal.any([
            controller.signal,
            automaticChecks.signal,
            leadership?.signal ?? controller.signal,
          ])
        : controller.signal;
      const attemptCurrent = () => !signal.aborted && current() && (leadership?.current() ?? true);
      if (
        !attemptCurrent() ||
        (automatic && coordinationFailed) ||
        scheduled.has(record.attemptId) ||
        !sameRecoveryScope(owner.scope, record)
      ) {
        return;
      }
      scheduled.add(record.attemptId);
      update((previous) => ({ ...previous, busy: record.attemptId }));
      void checkAttempt(
        record,
        owner,
        journal,
        signal,
        attemptCurrent,
        automatic,
        update,
        cache,
        (reply, lookupOwner) => {
          for (const observer of observers.current.get(record.operationId) ?? []) {
            observer(reply, lookupOwner);
          }
        },
      )
        .then((terminal) => {
          if (terminal) {
            observed.add(record.attemptId);
          }
        })
        .catch(unavailable)
        .finally(() => {
          scheduled.delete(record.attemptId);
          update((previous) => ({
            ...previous,
            busy: previous.busy === record.attemptId ? null : previous.busy,
          }));
        });
    };
    const scheduleCandidates = (
      operations: ReadonlyArray<RecoveryRecord>,
      handoffs: ReadonlyArray<RecoveryRecord>,
    ) => {
      const authority = leader?.current() === true ? leader : null;
      update((previous) => ({
        ...previous,
        operations: authority === null ? [...operations, ...handoffs] : operations,
      }));
      if (authority === null) {
        // A waiting editor needs its own authorized receipt after another tab settles.
        for (const record of handoffs) {
          if (observers.current.has(record.operationId)) {
            enqueue(record, true);
          }
        }
      } else {
        for (const record of [...operations, ...handoffs]) {
          enqueue(record, true, authority);
        }
      }
    };
    const schedule = () => {
      const seen = viewRef.current.owner === owner ? viewRef.current.finals : {};
      scheduleCandidates(
        pendingRecords,
        unobservedHandoffs(
          owner.scope,
          pendingRecords,
          confirmations,
          observed,
          seen,
          cache.invalidation(),
        ),
      );
    };
    const discover = async (
      name: string,
      read: (issue: (fault: JournalIssue) => void) => Promise<void>,
    ) => {
      if (listing.has(name) || !current()) {
        return;
      }
      listing.add(name);
      const issues = new Set<JournalIssue | "unavailable">();
      try {
        await read((fault) => {
          issues.add(fault);
        });
      } catch {
        issues.add("unavailable");
      } finally {
        listing.delete(name);
        if (current()) {
          issuesByRead.set(name, issues);
          const combined = new Set(
            [...issuesByRead.values()].flatMap((value) => Array.from(value)),
          );
          update((previous) => ({ ...previous, journalIssues: [...combined] }));
          if (name !== "diagnostics") {
            schedule();
          }
        }
      }
    };
    const run = () => {
      void discover("attempts", async (issue) => {
        pendingRecords = (await journal.list(owner.scope, issue)).filter((record) =>
          sameRecoveryScope(owner.scope, record),
        );
      });
      void discover("confirmations", async (issue) => {
        confirmations = (await journal.confirmed?.(owner.scope, issue)) ?? [];
      });
      if (journal.diagnose !== undefined) {
        void discover("diagnostics", async (issue) => {
          await journal.diagnose?.(issue);
        });
      }
    };
    const checkSelected = (record: RecoveryRecord) => {
      enqueue(record, false);
    };
    check.current = checkSelected;
    leadRecovery(
      owner.scope,
      controller.signal,
      (leadership) => {
        leader = leadership;
        run();
      },
      (coordinationUnavailable) => {
        coordinationFailed = coordinationUnavailable;
        if (coordinationUnavailable) {
          automaticChecks.abort();
        } else {
          automaticChecks = new AbortController();
          run();
        }
        update((previous) => ({ ...previous, coordinationUnavailable }));
      },
    );
    run();
    const timer = window.setInterval(run, 1000);
    const unsubscribe = subscribeRecovery(run);
    return () => {
      controller.abort();
      window.clearInterval(timer);
      unsubscribe();
      if (check.current === checkSelected) {
        check.current = () => undefined;
      }
    };
  }, [owner, journal, cache]);
  const visible = view.owner === owner && owner !== null ? view : empty;
  const disclosed = verified
    ? visible
    : {
        ...visible,
        notice: null,
        finals: {},
        presentations: {},
        coordinationUnavailable: false,
        journalIssues: [],
      };
  return {
    ...disclosed,
    // Invalidation hides disclosed results synchronously without aborting an identity-only check.
    finals: Object.fromEntries(
      Object.entries(disclosed.finals).filter(([, answer]) => answer.generation === generation),
    ),
    presentations: Object.fromEntries(
      Object.entries(disclosed.presentations)
        .filter(([, answer]) => answer.generation === generation)
        .map(([id, answer]) => [id, answer.view]),
    ),
    owner,
    subscribe,
    check: (record: RecoveryRecord) => {
      check.current(record);
    },
  };
};

const RecoveryContext = createContext<ReturnType<typeof useRecoveryNotice> | null>(null);
/** One authenticated workspace owns recovery across route and loading boundaries. */
export const RecoveryProvider = ({
  children,
  identity,
}: {
  children: ReactNode;
  identity: RecoveryOwnerIdentity | null;
}) => {
  const recovery = useRecoveryNotice(identity);
  return <RecoveryContext value={recovery}>{children}</RecoveryContext>;
};
export const useRecovery = () => {
  const recovery = useContext(RecoveryContext);
  if (recovery === null) {
    throw new Error("Recovery requires its workspace owner.");
  }
  return recovery;
};

export const useRecoverySubscription = () => useContext(RecoveryContext)?.subscribe ?? null;
