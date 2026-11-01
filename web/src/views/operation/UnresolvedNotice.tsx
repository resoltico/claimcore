import { useState } from "react";
import { AccessibleModal } from "../../components/AccessibleModal";
import { IdentifierValue } from "../../components/IdentifierValue";
import { useRecovery } from "../../recovery/RecoveryContext";
import type { RecoveryRecord } from "../../protocol/recoveryJournal";
import { NoticeView } from "../../presentation/Message";
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
            disabled={busy !== null}
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
  // Confirmation belongs to this view; leaving it discards unconfirmed intent, not recovery work.
  const [pending, setPending] = useState<Settlement | null>(null);
  const selected = pending?.owner === owner ? pending : null;
  const settlement = selected?.record ?? null;
  const requestSettlement = (record: RecoveryRecord) => {
    if (owner !== null && busy === null) {
      setPending({ owner, record });
    }
  };
  const cancelSettlement = () => {
    setPending(null);
  };
  const confirmSettlement = () => {
    if (selected !== null) {
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
        {settlement === null ? null : (
          <p>
            <IdentifierValue value={settlement.operationId} />
          </p>
        )}
        <button type="button" className="secondary-button" onClick={cancelSettlement}>
          {p.text("ui.keepChecking")}
        </button>
        <button type="button" onClick={confirmSettlement}>
          {p.text("ui.confirmSettlement")}
        </button>
      </AccessibleModal>
    </section>
  );
};
