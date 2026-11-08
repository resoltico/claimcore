import { useCallback, useEffect, useRef, useLayoutEffect, useEffectEvent } from "react";
import type { OperationReceipt } from "@contracts/client";
import type { CurrentCase } from "../../protocol/types";
import type { FormValues } from "../../protocol/forms";
import { useCredential } from "../../session/SessionContext";
import { useOperation } from "../../hooks/operation/useOperation";
import { useOperationNavigation } from "../../hooks/useOperationNavigation";
import { useEndpointCache } from "../../hooks/EndpointProvider";
import { OperationPanel, OperationDiscard } from "../operation/OperationPanel";

type Props = {
  current: CurrentCase;
  disclosureAvailable: boolean;
  subject: "WORK" | "REMINDER" | "NOTE";
  kind: string;
  targetIdentity?: string;
  completionAnchor?: string;
  prefill?: FormValues;
  onClose: () => void;
  onRecorded: (receipt: OperationReceipt) => void;
  onLockChange: (locked: boolean) => void;
  onDisclosureRefused: () => void;
};
/** Reuses the same frozen review, journal-before-send, retry and scoped disclosure coordinator. */
export const WorkOperation = ({
  current,
  disclosureAvailable,
  subject,
  kind,
  targetIdentity,
  completionAnchor,
  prefill,
  onClose,
  onRecorded,
  onLockChange,
  onDisclosureRefused,
}: Props) => {
  const credential = useCredential();
  const anchor = useRef<HTMLElement | null>(
    completionAnchor === undefined ? null : document.getElementById(completionAnchor),
  );
  const previewed = useRef(false);
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
    onDisclosureRefused,
  });
  useEffect(() => {
    if (
      ["REMINDER_COMPLETE", "ADD_NOTE", "REMOVE_NOTE"].includes(kind) &&
      !previewed.current &&
      model.ownerVerified &&
      model.disclosureCurrent
    ) {
      previewed.current = true;
      void model.preview();
    }
  }, [kind, model]);
  const invalidate = useEffectEvent(() => {
    model.invalidateDisclosure();
  });
  useLayoutEffect(() => {
    if (!disclosureAvailable) {
      invalidate();
    }
  }, [disclosureAvailable]);
  const navigation = useOperationNavigation(model, onClose);
  useEffect(() => {
    if (model.ownerVerified && model.receipt !== null && model.receipt.subject.kind === subject) {
      navigation.committed();
      accepted(model.receipt);
    }
  }, [model.receipt, model.ownerVerified, subject, navigation, accepted]);
  const observed = model.currentCase;
  const workOffered =
    observed !== null && (observed.work.canUpdateSummary || observed.work.canAssign);
  // Reminder offers originate in the selected current reminder row; preview and commit recheck them.
  const offered = subject === "WORK" && !workOffered ? [] : [kind];
  return (
    <>
      <OperationDiscard model={model} onClose={navigation.close} />
      <OperationPanel
        model={model}
        current={observed}
        offered={offered}
        onClose={navigation.close}
        fixedTarget
        {...(subject === "NOTE" ? { fixedPaths: ["act.noteId", "act.text"] } : {})}
        {...(completionAnchor === undefined ? {} : { completionAnchor: anchor })}
        disclosureAvailable={disclosureAvailable}
      />
    </>
  );
};
