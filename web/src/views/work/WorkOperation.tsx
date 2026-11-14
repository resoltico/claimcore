import { seededAffordanceValues, type OperationAffordance } from "../../operation/affordances";
import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { useCallback, useEffect, useRef, useLayoutEffect, useEffectEvent, useMemo } from "react";
import type { TaskId, OperationBinding } from "@contracts/client";
import type { OperationReceipt, CurrentCase } from "../../protocol/types";

import type { FormValues } from "../../protocol/forms";
import { useCredential } from "../../session/SessionContext";
import { useOperation } from "../../hooks/operation/useOperation";
import { useOperationNavigation } from "../../hooks/useOperationNavigation";
import { useEndpointCache } from "../../hooks/EndpointProvider";
import { OperationPanel, OperationDiscard } from "../operation/OperationPanel";

type Props = {
  affordance?: OperationAffordance;
  current: CurrentCase;
  disclosureAvailable: boolean;
  subject: "WORK" | "REMINDER" | "NOTE";
  kind: string;
  targetIdentity?: string;
  completionAnchor?: string | undefined;
  prefill?: FormValues;
  binding?: OperationBinding | undefined;
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
  affordance,
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
  const filled = useMemo(
    () => ({
      ...(affordance === undefined ? {} : seededAffordanceValues(affordance)),
      ...prefill,
    }),
    [affordance, prefill],
  );
  const model = useOperation({
    subject,
    kind,
    current,
    credential,
    affordance,
    prefill: filled,
    binding,
    targetIdentity,
    onLockChange,
    onDisclosureRefused,
  });
  const observed = model.currentCase;
  const allowed = model.affordance?.blocked === null;
  const authored = model.task.inputs.filter((input) => input.source === "AUTHORED");
  const autoPreview =
    authored.length === 0 ||
    model.affordance?.editable.length === 0 ||
    authored.every(
      (input) =>
        Object.hasOwn(model.affordance?.seeds ?? {}, input.id) ||
        Object.hasOwn(prefill ?? {}, input.id),
    );
  useEffect(() => {
    if (
      allowed &&
      autoPreview &&
      !previewed.current &&
      model.ownerVerified &&
      model.disclosureCurrent
    ) {
      previewed.current = true;
      void model.preview();
    }
  }, [model, allowed, autoPreview]);
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
      model.prepared?.input.binding.kind === subject &&
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
