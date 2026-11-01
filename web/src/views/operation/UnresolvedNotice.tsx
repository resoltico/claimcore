import { useRequesterContext } from "../../hooks/useRequesterContext";
import { useState } from "react";
import { AccessibleModal } from "../../components/AccessibleModal";
import { IdentifierValue } from "../../components/IdentifierValue";
import { useRecovery } from "../../recovery/RecoveryContext";
import type { RecoveryRecord } from "../../protocol/recoveryJournal";
import { NoticeView } from "../../presentation/Message";
import { operationEntry } from "../../protocol/operationCatalog";
import { renderCatalogKey } from "../../presentation/messages";
import { usePresentation } from "../../presentation/context";

type Settlement = {
  owner: NonNullable<ReturnType<typeof useRecovery>["owner"]>;
  record: RecoveryRecord;
};
const RecoveryRows = ({
  showEmpty,
  requestSettlement,
}: {
  showEmpty: boolean;
  requestSettlement: (record: RecoveryRecord) => void;
}) => {
  const p = usePresentation();
  const { operations, busy, notice, cleanup, check } = useRecovery();
  const paused = useRequesterContext()?.maintenance.active === true;
  return (
    <>
      {showEmpty && operations.length === 0 && notice === null ? (
        <p>{p.text("ui.recoveryEmpty")}</p>
      ) : null}
      {operations.map((record) => (
        <div key={record.attemptId}>
          <p>{p.text("ui.savedRecoveryAttempt")}</p>
          <p>
            <IdentifierValue value={record.operationId} />
            {record.subject === "CASE" ? p.actLabel(record.act) : p.authorityLabel(record.act)}
          </p>
          <button type="button" disabled={busy !== null} onClick={() => void check(record)}>
            {p.text("ui.checkUnresolvedOperation")}
          </button>
          <button
            type="button"
            disabled={busy !== null || paused}
            onClick={() => {
              requestSettlement(record);
            }}
          >
            {p.text("endpoint.operation.resolve.label")}
          </button>
        </div>
      ))}
      {notice === null ? null : <NoticeView value={notice} />}
      {cleanup === null ? null : (
        <p role="alert">
          <NoticeView value={cleanup} />
        </p>
      )}
    </>
  );
};
export const UnresolvedNotice = ({ showEmpty = false }: { showEmpty?: boolean }) => {
  const p = usePresentation();
  const { operations, busy, notice, owner, settle } = useRecovery();
  const paused = useRequesterContext()?.maintenance.active === true;
  // Confirmation belongs to this view; leaving it discards unconfirmed intent, not recovery work.
  const [pending, setPending] = useState<Settlement | null>(null);
  const selected = pending?.owner === owner ? pending : null;
  const settlement = selected?.record ?? null;
  const requestSettlement = (record: RecoveryRecord) => {
    if (owner !== null && busy === null && !paused) {
      setPending({ owner, record });
    }
  };
  const cancelSettlement = () => {
    setPending(null);
  };
  const confirmSettlement = () => {
    if (selected !== null && !paused) {
      setPending(null);
      void settle(selected.record, selected.owner);
    }
  };
  if (!showEmpty && operations.length === 0 && notice === null) {
    return null;
  }
  return (
    <section role="status" aria-busy={busy !== null}>
      <RecoveryRows showEmpty={showEmpty} requestSettlement={requestSettlement} />
      <AccessibleModal
        title={p.text("endpoint.operation.resolve.label")}
        description={p.text("endpoint.operation.resolve.meaning")}
        closeLabel={p.text("ui.close")}
        isOpen={settlement !== null}
        isDismissable
        onOpenChange={cancelSettlement}
      >
        {selected === null ? null : (
          <>
            <dl>
              <div>
                <dt>{p.fieldLabel("actorId")}</dt>
                <dd>
                  <IdentifierValue value={selected.owner.scope.actorId} />
                </dd>
              </div>
              <div>
                <dt>{p.term("scope", "INSTALLATION")}</dt>
                <dd>
                  <IdentifierValue value={selected.owner.scope.installationId} />
                </dd>
              </div>
              <div>
                <dt>{p.fieldLabel("operationId")}</dt>
                <dd>
                  <IdentifierValue value={selected.record.operationId} />
                </dd>
              </div>
            </dl>
            <p>
              {renderCatalogKey(
                p,
                operationEntry({ subject: "OPERATION", kind: "operation.resolve" }).confirmationId!,
              )}
            </p>
          </>
        )}
        <button type="button" className="secondary-button" onClick={cancelSettlement}>
          {p.text("ui.keepChecking")}
        </button>
        <button type="button" disabled={paused} onClick={confirmSettlement}>
          {p.text("ui.confirmSettlement")}
        </button>
      </AccessibleModal>
    </section>
  );
};
