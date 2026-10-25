import { useState } from "react";
import { unresolvedOperation } from "./unresolvedOperation";
import { service, resultNotice } from "../../api/service";
import type { Notice } from "../../api/notices";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";

/** Reload retains an identity only: there are no request bytes to send again. */
export const UnresolvedNotice = ({ token }: { token: string }) => {
  const p = usePresentation();
  const [operation, setOperation] = useState(unresolvedOperation.read);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const check = async () => {
    if (operation === null || busy) {
      return;
    }
    setBusy(true);
    const result = await service.resolve(operation.operationId, token);
    setBusy(false);
    if (
      result.kind === "outcome" &&
      (result.value.outcome.tag === "ACCEPTED" || result.value.outcome.tag === "NOT_ACCEPTED")
    ) {
      unresolvedOperation.clear(operation.operationId);
      setOperation(null);
      setNotice(
        result.value.outcome.tag === "ACCEPTED"
          ? { kind: "accepted", operationId: operation.operationId }
          : { kind: "local", reason: "notAccepted" },
      );
    } else {
      setNotice(resultNotice(result));
    }
  };
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
            {operation.scope === "CASE"
              ? p.actLabel(operation.act)
              : p.authorityLabel(operation.act)}
          </p>
          <button type="button" disabled={busy} onClick={() => void check()}>
            {p.text("ui.checkOutcome")}
          </button>
        </>
      )}
      {notice === null ? null : <NoticeView value={notice} />}
    </section>
  );
};
