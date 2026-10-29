import { useCredential } from "../../session/SessionContext";
import { useMemo, useState, useSyncExternalStore } from "react";
import { unresolvedOperation } from "./unresolvedOperation";
import { service, resultNotice } from "../../api/service";
import type { Notice } from "../../protocol/notices";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";

/** Reload retains an identity only: there are no request bytes to send again. */
const useRecoveryNotice = () => {
  const credential = useCredential();
  const snapshot = useSyncExternalStore(
    unresolvedOperation.subscribe,
    unresolvedOperation.snapshot,
  );
  const operation = useMemo(() => unresolvedOperation.read(snapshot), [snapshot]);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const check = async () => {
    if (operation === null || busy) {
      return;
    }
    setBusy(true);
    const result = await service.resolve(operation.operationId, credential);
    setBusy(false);
    if (
      result.kind === "outcome" &&
      (result.value.outcome.kind === "ACCEPTED" || result.value.outcome.kind === "NOT_ACCEPTED")
    ) {
      unresolvedOperation.clear(operation.operationId);
      setNotice(
        result.value.outcome.kind === "ACCEPTED"
          ? { kind: "accepted", operationId: operation.operationId }
          : { kind: "local", reason: "notAccepted" },
      );
    } else {
      setNotice(resultNotice(result));
    }
  };
  return { operation, busy, notice, check };
};
export const UnresolvedNotice = () => {
  const p = usePresentation();
  const { operation, busy, notice, check } = useRecoveryNotice();
  if (operation === null && notice === null) {
    return null;
  }
  return (
    <section role="status" aria-busy={busy}>
      {operation === null ? null : (
        <>
          <p>{p.text("notice.outcomeUnknown")}</p>
          <p>
            <bdi>{operation.operationId}</bdi>
            {operation.subject === "CASE"
              ? p.actLabel(operation.act)
              : p.authorityLabel(operation.act)}
          </p>
          <button type="button" disabled={busy} onClick={() => void check()}>
            {p.text("ui.checkUnresolvedOperation")}
          </button>
        </>
      )}
      {notice === null ? null : <NoticeView value={notice} />}
    </section>
  );
};
