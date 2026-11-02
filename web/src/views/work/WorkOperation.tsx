import { useCallback, useEffect, useRef } from "react";
import type { OperationReceipt } from "@contracts/client";
import type { CurrentCase } from "../../protocol/types";
import type { FormValues } from "../../protocol/forms";
import { useCredential } from "../../session/SessionContext";
import { usePresentation } from "../../presentation/context";
import { useOperation } from "../../hooks/operation/useOperation";
import { useOperationNavigation } from "../../hooks/useOperationNavigation";
import { useEndpointCache } from "../../hooks/EndpointProvider";
import { renderCatalogKey } from "../../presentation/messages";
import { operationEntry } from "../../protocol/operationCatalog";
import { OperationPanel, OperationDiscard } from "../operation/OperationPanel";

type Props = {
  current: CurrentCase;
  subject: "WORK" | "REMINDER";
  kind: string;
  targetIdentity?: string;
  prefill?: FormValues;
  onClose: () => void;
  onRecorded: (receipt: OperationReceipt) => void;
  onLockChange: (locked: boolean) => void;
};
/** Reuses the same frozen review, journal-before-send, retry and scoped disclosure coordinator. */
export const WorkOperation = ({
  current,
  subject,
  kind,
  targetIdentity,
  prefill,
  onClose,
  onRecorded,
  onLockChange,
}: Props) => {
  const p = usePresentation();
  const credential = useCredential();
  const cache = useEndpointCache();
  const recorded = useRef<string | null>(null);
  const accepted = useCallback(
    (receipt: OperationReceipt) => {
      if (recorded.current !== receipt.operationId) {
        recorded.current = receipt.operationId;
        cache?.invalidate();
        onRecorded(receipt);
      }
    },
    [cache, onRecorded],
  );
  const model = useOperation({
    subject,
    kind,
    current,
    credential,
    prefill: { "target.kind": "ID", "target.caseId": current.caseId, ...prefill },
    ...(targetIdentity === undefined ? {} : { targetIdentity }),
    onLockChange,
  });
  const navigation = useOperationNavigation(model, onClose);
  useEffect(() => {
    if (model.ownerVerified && model.receipt !== null && model.receipt.subject.kind === subject) {
      navigation.committed();
      accepted(model.receipt);
    }
  }, [model.receipt, model.ownerVerified, subject, navigation, accepted]);
  const definition = operationEntry(model.entry);
  const observed = model.currentCase;
  const workOffered =
    observed !== null && (observed.work.canUpdateSummary || observed.work.canAssign);
  // Reminder offers originate in the selected current reminder row; preview and commit recheck them.
  const offered = subject === "WORK" && !workOffered ? [] : [kind];
  return (
    <section className="work-operation" aria-labelledby="work-operation-title">
      <h2 id="work-operation-title" tabIndex={-1}>
        {renderCatalogKey(p, definition.labelId)}
      </h2>
      {definition.meaningId === null ? null : <p>{renderCatalogKey(p, definition.meaningId)}</p>}
      <OperationDiscard model={model} onClose={navigation.close} />
      <OperationPanel
        model={model}
        current={observed}
        offered={offered}
        onClose={navigation.close}
        fixedTarget
      />
    </section>
  );
};
