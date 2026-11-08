import { IdentifierValue } from "../../components/IdentifierValue";
import { useRecovery } from "../../recovery/RecoveryContext";
import { NoticeView } from "../../presentation/Message";
import { operationEntryForInput } from "../../protocol/operationCatalog";
import { renderCatalogKey } from "../../presentation/messages";
import { usePresentation } from "../../presentation/context";

export const UnresolvedNotice = ({ showEmpty = false }: { showEmpty?: boolean }) => {
  const p = usePresentation();
  const { operations, deadlines, busy, notice, cleanup, check } = useRecovery();
  if (!showEmpty && operations.length === 0 && notice === null) {
    return null;
  }
  return (
    <section role="status" aria-busy={busy !== null}>
      {showEmpty && operations.length === 0 && notice === null ? (
        <p>{p.text("ui.recoveryEmpty")}</p>
      ) : null}
      {operations.map((record) => (
        <div className="recovery-attempt" key={record.attemptId}>
          <p>{p.text("ui.savedRecoveryAttempt")}</p>
          <p>
            <IdentifierValue value={record.operationId} />
            {renderCatalogKey(
              p,
              operationEntryForInput({ kind: record.subject, act: { kind: record.act } }).labelId,
            )}
          </p>
          {deadlines[record.attemptId] === undefined ? null : (
            <p>{p.text("ui.unconfirmedUntil", { finalAt: deadlines[record.attemptId]! })}</p>
          )}
          <button
            type="button"
            disabled={busy !== null}
            onClick={() => {
              check(record);
            }}
          >
            {p.text("ui.checkUnresolvedOperation")}
          </button>
        </div>
      ))}
      {notice === null ? null : <NoticeView value={notice} />}
      {cleanup === null ? null : (
        <p role="alert">
          <NoticeView value={cleanup} />
        </p>
      )}
    </section>
  );
};
