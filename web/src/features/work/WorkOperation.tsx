import { isNarrativeCreationId } from "../../operation/metadata";
import { useInlineNoteDraft, type NoteIntent } from "../case/InlineNoteDraft";
import { initialAffordanceSeeds, type OperationAffordance } from "../../operation/affordances";
import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { useCallback, useEffect, useRef, useLayoutEffect, useEffectEvent } from "react";
import type { TaskId, OperationBinding } from "@contracts/client";
import type { OperationReceipt, CurrentCase } from "../../protocol/types";

import type { FormValues } from "../../protocol/forms";
import { useCredential } from "../../session/SessionContext";
import { useOperation } from "../../operation/useOperation";
import { useOperationNavigation } from "../../hooks/useOperationNavigation";
import { useEndpointCache } from "../../hooks/EndpointProvider";
import { OperationPanel, OperationDiscard } from "../operation/OperationPanel";

type Props = {
  noteIntent?: NoteIntent | undefined;
  implicit?: boolean | undefined;
  upload?: File | undefined;
  affordance?: OperationAffordance;
  current: CurrentCase;
  disclosureAvailable: boolean;
  subject: "WORK" | "REMINDER" | "NOTE" | "LINK" | "ATTACHMENT";
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
const showWorkEditor = (implicit: boolean, model: ReturnType<typeof useOperation>) =>
  !implicit ||
  model.priorUncertain ||
  model.notice !== null ||
  Object.keys(model.fieldErrors).length > 0;

/** Reuses the same frozen review, journal-before-send, retry and scoped disclosure coordinator. */
export const WorkOperation = ({
  current,
  noteIntent,
  implicit = false,
  upload,
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
  const { bind: bindNote } = useInlineNoteDraft();
  const { caseId } = current.subject;
  const credential = useCredential();
  const anchor = useRef<HTMLElement | null>(
    completionAnchor === undefined ? null : document.getElementById(completionAnchor),
  );
  const previewed = useRef(false);
  const sent = useRef<string | null>(null);
  const cache = useEndpointCache();
  const recorded = useRef<string | null>(null);
  const accepted = useCallback(
    (receipt: OperationReceipt, operationId: string) => {
      if (recorded.current !== operationId) {
        recorded.current = operationId;
        if (
          receipt.subject.kind === "RECEIPT" &&
          receipt.attachmentObservation?.caseId === caseId
        ) {
          cache?.observeAttachmentReceipt(receipt);
        } else {
          cache?.invalidate();
        }
        onRecorded(receipt, operationId);
      }
    },
    [cache, caseId, onRecorded],
  );
  const model = useOperation({
    subject,
    upload,
    kind,
    current,
    credential,
    affordance,
    prefill,
    binding,
    targetIdentity,
    onLockChange,
    onDisclosureRefused,
  });
  useLayoutEffect(() => {
    if (
      noteIntent !== undefined &&
      model.prepared !== null &&
      isNarrativeCreationId(model.prepared.input.task)
    ) {
      bindNote(noteIntent, model.prepared.input.operationId);
    }
  }, [bindNote, noteIntent, model.prepared]);
  const implicitReview = implicit && model.task.review === "IMPLICIT";
  const observed = model.currentCase;
  const allowed = model.affordance?.blocked === null;
  const authored = model.task.inputs.filter((input) => input.source === "AUTHORED");
  const acquiredSeeds =
    model.affordance === undefined ? {} : initialAffordanceSeeds(model.affordance);
  const autoPreview =
    authored.length === 0 ||
    model.affordance?.editable.length === 0 ||
    authored.every(
      (input) =>
        Object.hasOwn(model.values, input.id) &&
        ((Object.hasOwn(acquiredSeeds, input.id) &&
          model.affordance?.editable.some((id) => id === input.id) === false) ||
          Object.hasOwn(prefill ?? {}, input.id)),
    );
  useEffect(() => {
    if (
      allowed &&
      (autoPreview || implicitReview) &&
      !previewed.current &&
      model.ownerVerified &&
      model.disclosureCurrent
    ) {
      previewed.current = true;
      void model.preview();
    }
  }, [model, allowed, autoPreview, implicitReview]);
  useEffect(() => {
    const operation = model.prepared?.input.operationId;
    if (
      implicitReview &&
      allowed &&
      model.ownerVerified &&
      model.disclosureCurrent &&
      model.review !== null &&
      model.phase === "REVIEWING" &&
      model.notice === null &&
      operation !== undefined &&
      operation !== sent.current
    ) {
      sent.current = operation;
      model.confirm(true);
      void model.execute();
    }
  }, [model, allowed, implicitReview]);
  const invalidate = useEffectEvent(() => {
    model.invalidateDisclosure();
  });
  useLayoutEffect(() => {
    if (!disclosureAvailable) {
      invalidate();
    }
  }, [disclosureAvailable]);
  const navigation = useOperationNavigation(model, onClose);
  useLayoutEffect(() => {
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
        open={showWorkEditor(implicitReview, model)}
        current={observed}
        offered={allowed ? [kind] : []}
        onClose={navigation.close}
        {...(completionAnchor === undefined ? {} : { completionAnchor: anchor })}
        disclosureAvailable={disclosureAvailable}
        capturedInputs={upload !== undefined}
      />
    </>
  );
};
