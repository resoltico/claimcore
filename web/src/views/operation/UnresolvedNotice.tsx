import { IdentifierValue } from "../../components/IdentifierValue";
import { CopyValue } from "../../components/CopyValue";
import { useRecovery } from "../../recovery/RecoveryContext";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { NoticeView } from "../../presentation/Message";
import { operationEntryForInput } from "../../protocol/operationCatalog";
import { renderCatalogKey } from "../../presentation/messages";
import { usePresentation } from "../../presentation/context";
import { ScreenView } from "../../ui/ScreenView";
import "./unconfirmed.css";

export const UnresolvedNotice = ({ showEmpty = false }: { showEmpty?: boolean }) => {
  const p = usePresentation();
  const requester = useRequesterContext();
  const { operations, presentations, busy, notice, cleanup, check } = useRecovery();
  if (!showEmpty && operations.length === 0 && notice === null) {
    return null;
  }
  return (
    <section className="unconfirmed-list" role="status" aria-busy={busy !== null}>
      {showEmpty && operations.length === 0 && notice === null ? (
        <p>{p.text("ui.recoveryEmpty")}</p>
      ) : null}
      <ul>
        {operations.map((record) => (
          <li className="recovery-attempt" key={record.attemptId}>
            <div className="recovery-card-heading">
              <span className="recovery-state" data-state="CHECKING">
                {p.text(cleanup === null ? "ui.checking" : "ui.savedRecoveryAttempt")}
              </span>
              <strong>
                {renderCatalogKey(
                  p,
                  operationEntryForInput({ kind: record.subject, act: { kind: record.act } })
                    .labelId,
                )}
              </strong>
            </div>
            {presentations[record.attemptId] === undefined ? null : (
              <ScreenView
                view={presentations[record.attemptId]!}
                timeZone={requester?.timeZone ?? "UTC"}
              />
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
            <details data-region="DETAILS">
              <summary>{p.text("label.recordDetails")}</summary>
              <dl>
                <dt>{p.fieldLabel("operationId")}</dt>
                <dd>
                  <IdentifierValue value={record.operationId} />
                  <CopyValue label={p.fieldLabel("operationId")} value={record.operationId} />
                </dd>
              </dl>
            </details>
          </li>
        ))}
      </ul>
      {notice === null ? null : (
        <div className="recovery-feedback">
          <NoticeView value={notice} />
        </div>
      )}
      {cleanup === null ? null : (
        <p role="alert">
          <NoticeView value={cleanup} />
        </p>
      )}
    </section>
  );
};
