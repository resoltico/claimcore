import { IdentifierValue } from "../../components/IdentifierValue";
import { useCredential, useSessionContext } from "../../session/SessionContext";
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { useRecoveryJournal } from "../../recovery/JournalContext";
import { subscribeRecovery } from "../../recovery/indexedJournal";
import {
  sameRecoveryScope,
  type RecoveryRecord,
  type RecoveryScope,
} from "../../protocol/recoveryJournal";
import { service, resultNotice } from "../../api/service";
import type { Notice } from "../../protocol/notices";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";

type RecoveryView = {
  scope: RecoveryScope | null;
  operations: ReadonlyArray<RecoveryRecord>;
  failed: boolean;
};
type Feedback = { scope: RecoveryScope; credential: string; epoch: number; notice: Notice };
const belongsToView = (
  scope: RecoveryScope | null,
  credential: string,
  epoch: number,
  value: { scope: RecoveryScope | null; credential?: string; epoch?: number } | null,
) =>
  value?.scope !== undefined &&
  value.scope !== null &&
  scope !== null &&
  sameRecoveryScope(value.scope, scope) &&
  (value.credential === undefined || value.credential === credential) &&
  (value.epoch === undefined || value.epoch === epoch);

const finalResolution = (result: Awaited<ReturnType<typeof service.resolve>>) =>
  result.kind === "outcome" &&
  (result.value.outcome.kind === "ACCEPTED" || result.value.outcome.kind === "NOT_ACCEPTED");
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
  const scope = useMemo<RecoveryScope | null>(
    () =>
      requester === null
        ? null
        : {
            serviceOrigin: location.origin,
            installationId: requester.installationId,
            actorId: requester.actorId,
          },
    [requester],
  );
  const latest = useRef({ credential, epoch, scope });
  useLayoutEffect(() => {
    latest.current = { credential, epoch, scope };
  }, [credential, epoch, scope]);
  const [view, setView] = useState<RecoveryView>({ scope: null, operations: [], failed: false });
  const [busy, setBusy] = useState<{
    scope: RecoveryScope;
    credential: string;
    epoch: number;
    attempt: string;
  } | null>(null);
  const [feedback, setFeedback] = useState<Feedback | null>(null);
  const owns = (value: Parameters<typeof belongsToView>[3]) =>
    belongsToView(scope, credential, epoch, value);
  useEffect(() => {
    let active = true;
    let serial = 0;
    if (scope === null) {
      return;
    }
    const currentScope = scope;
    const refresh = () => {
      const request = ++serial;
      void Promise.resolve()
        .then(() => journal.list(currentScope))
        .then((operations) => {
          if (active && request === serial) {
            setView({ scope: currentScope, operations, failed: false });
          }
        })
        .catch(() => {
          if (active && request === serial) {
            setView({ scope: currentScope, operations: [], failed: true });
          }
        });
    };
    refresh();
    const unsubscribe = subscribeRecovery(refresh);
    return () => {
      active = false;
      unsubscribe();
    };
  }, [scope, journal]);
  const check = async (record: RecoveryRecord) => {
    const current = () =>
      alive.current &&
      latest.current.credential === credential &&
      latest.current.epoch === epoch &&
      latest.current.scope === scope &&
      scope !== null &&
      sameRecoveryScope(scope, record);
    if (scope === null || !current() || owns(busy)) {
      return;
    }
    setBusy({ scope, credential, epoch, attempt: record.attemptId });
    const result = await service.resolve(record.operationId, credential, record.installationId);
    if (!current()) {
      return;
    }
    setBusy(null);
    let notice = resultNotice(result);
    if (finalResolution(result) && result.kind === "outcome") {
      try {
        await journal.remove(record);
        if (!current()) {
          return;
        }
        setView((previous) => ({
          ...previous,
          operations: previous.operations.filter((value) => value.attemptId !== record.attemptId),
        }));
        notice =
          result.value.outcome.kind === "ACCEPTED"
            ? { kind: "accepted", operationId: record.operationId }
            : { kind: "local", reason: "notAccepted" };
      } catch {
        notice = { kind: "local", reason: "recoveryListingUnavailable" };
      }
    }
    if (current()) {
      setFeedback({ scope, credential, epoch, notice });
    }
  };
  const operations = owns(view)
    ? view.operations.filter((record) => scope !== null && sameRecoveryScope(scope, record))
    : [];
  const ownedFeedback = owns(feedback) ? feedback : null;
  const ownedBusy = owns(busy) ? busy : null;
  const unavailable: Notice | null =
    owns(view) && view.failed ? { kind: "local", reason: "recoveryListingUnavailable" } : null;
  return {
    operations,
    busy: ownedBusy?.attempt ?? null,
    notice: ownedFeedback?.notice ?? unavailable,
    check,
  };
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
