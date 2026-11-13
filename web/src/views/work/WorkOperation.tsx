import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { useCallback, useEffect, useRef, useLayoutEffect, useEffectEvent } from "react";
import type { TaskId } from "@contracts/client";
import type { OperationReceipt, CurrentCase } from "../../protocol/types";
import type { TaskBindingSource } from "../../operation/taskForm";
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
  completionAnchor?: string | undefined;
  prefill?: FormValues;
  binding?: TaskBindingSource | undefined;
  tasks?: ReadonlyArray<TaskId> | undefined;
  onClose: () => void;
  onRecorded: (receipt: OperationReceipt, operationId: string) => void;
  onLockChange: MutationLockChange;
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
  binding,
  tasks,
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
    (receipt: OperationReceipt, operationId: string) => {
      if (recorded.current !== operationId) {
        recorded.current = operationId;
        cache?.invalidate();
        onRecorded(receipt, operationId);
      }
    },
    [cache, onRecorded],
  );
  const model = useOperation({
    subject,
    kind,
    current,
    credential,
    ...(prefill === undefined ? {} : { prefill }),
    ...(binding === undefined ? {} : { binding }),
    ...(targetIdentity === undefined ? {} : { targetIdentity }),
    onLockChange,
    onDisclosureRefused,
  });
  const observed = model.currentCase;
  const allowed = (tasks ?? observed?.context.tasks ?? []).includes(model.task.id);
  useEffect(() => {
    if (
      allowed &&
      ["reminder.complete", "note.add", "note.remove"].includes(kind) &&
      !previewed.current &&
      model.ownerVerified &&
      model.disclosureCurrent
    ) {
      previewed.current = true;
      void model.preview();
    }
  }, [kind, model, allowed]);
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
    if (
      model.ownerVerified &&
      model.receipt !== null &&
      model.receipt.context.target.kind === subject &&
      model.prepared !== null
    ) {
      navigation.committed();
      accepted(model.receipt, model.prepared.input.operationId);
    }
  }, [model.receipt, model.prepared, model.ownerVerified, subject, navigation, accepted]);
  return (
    <>
      <OperationDiscard model={model} onClose={navigation.close} />
      <OperationPanel
        model={model}
        current={observed}
        offered={allowed ? [kind] : []}
        onClose={navigation.close}
        {...(completionAnchor === undefined ? {} : { completionAnchor: anchor })}
        disclosureAvailable={disclosureAvailable}
      />
    </>
  );
};
