import { IdentifierValue } from "../../components/IdentifierValue";
import { useCredential, useSessionContext } from "../../session/SessionContext";
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { useRecoveryJournal } from "../../recovery/JournalContext";
import { subscribeRecovery } from "../../recovery/indexedJournal";
import {
  sameRecoveryScope,
  type RecoveryRecord,
  type RecoveryJournal,
  type RecoveryScope,
} from "../../protocol/recoveryJournal";
import { service, resultNotice } from "../../api/service";
import type { Notice } from "../../protocol/notices";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";

type RecoveryOwner = { scope: RecoveryScope; credential: string; epoch: number };
type RecoveryView = {
  owner: RecoveryOwner | null;
  operations: ReadonlyArray<RecoveryRecord>;
  failed: boolean;
};
type RecoveryAttempt = { owner: RecoveryOwner; attempt: string };
type Feedback = { owner: RecoveryOwner; notice: Notice };
const finalResolution = (result: Awaited<ReturnType<typeof service.resolve>>) =>
  result.kind === "outcome" &&
  (result.value.outcome.kind === "ACCEPTED" || result.value.outcome.kind === "NOT_ACCEPTED");
const settleRecovery = async (
  record: RecoveryRecord,
  owner: RecoveryOwner,
  journal: RecoveryJournal,
  current: () => boolean,
): Promise<{ notice: Notice; removed: boolean } | null> => {
  const result = await service.resolve(record.operationId, owner.credential, record.installationId);
  if (!current()) {
    return null;
  }
  if (!finalResolution(result) || result.kind !== "outcome") {
    return { notice: resultNotice(result), removed: false };
  }
  try {
    await journal.remove(record);
  } catch {
    return { notice: { kind: "local", reason: "recoveryListingUnavailable" }, removed: false };
  }
  if (!current()) {
    return null;
  }
  return {
    removed: true,
    notice:
      result.value.outcome.kind === "ACCEPTED"
        ? { kind: "accepted", operationId: record.operationId }
        : { kind: "local", reason: "notAccepted" },
  };
};
const visibleRecovery = (
  owner: RecoveryOwner | null,
  view: RecoveryView,
  busy: RecoveryAttempt | null,
  feedback: Feedback | null,
) => {
  if (owner === null) {
    return { operations: [], busy: null, notice: null };
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
  const check = async (record: RecoveryRecord) => {
    const current = () => alive.current && owner !== null && latest.current === owner;
    if (
      owner === null ||
      !current() ||
      busy?.owner === owner ||
      !sameRecoveryScope(owner.scope, record)
    ) {
      return;
    }
    const attempt: RecoveryAttempt = { owner, attempt: record.attemptId };
    setBusy(attempt);
    try {
      const settled = await settleRecovery(record, owner, journal, current);
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
        setFeedback({ owner, notice: settled.notice });
      }
    } catch {
      if (current()) {
        setFeedback({ owner, notice: { kind: "local", reason: "unreachable" } });
      }
    } finally {
      // An obsolete completion must never release a newer attempt's lock.
      setBusy((previous) => (previous === attempt ? null : previous));
    }
  };
  return { ...visibleRecovery(owner, view, busy, feedback), check };
};
export const UnresolvedNotice = () => {
  const p = usePresentation();
  const { operations, busy, notice, check } = useRecoveryNotice();
  if (operations.length === 0 && notice === null) {
    return null;
  }
  return (
    <section role="status" aria-busy={busy !== null}>
      {operations.map((record) => (
        <div key={record.attemptId}>
          <p>{p.text("notice.outcomeUnknown")}</p>
          <p>
            <IdentifierValue value={record.operationId} />
            {record.subject === "CASE" ? p.actLabel(record.act) : p.authorityLabel(record.act)}
          </p>
          <button type="button" disabled={busy !== null} onClick={() => void check(record)}>
            {p.text("ui.checkUnresolvedOperation")}
          </button>
        </div>
      ))}
      {notice === null ? null : <NoticeView value={notice} />}
    </section>
  );
};
