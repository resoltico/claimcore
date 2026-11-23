import { clientCompositions } from "@contracts/client";
import { operationTask } from "../../protocol/taskCatalog";
import { isNarrativeCreationId, narrativeCreationInput } from "../../operation/metadata";
import {
  type BoundOperationAffordance,
  isOperationAffordance,
  type OperationAffordance,
} from "../../operation/affordances";
import { useInlineNoteDraft, type NoteIntent } from "../case/InlineNoteDraft";
import { RoutedReminder } from "./SelectedReminder";
import { useAcceptedOperation } from "../../hooks/useAcceptedOperation";
import { ReceiptFeedback } from "../operation/ReceiptFeedback";
import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { useMemo, useRef, useState, useEffect } from "react";
import type { Affordance, TaskId, OperationBinding } from "@contracts/client";
import type { OperationReceipt, CurrentCase } from "../../protocol/types";
import type { ReminderActHandler } from "./reminderActions";

import type { FormValues } from "../../protocol/forms";
import { CaseNextActions } from "./CaseNextActions";
import { WorkOperation } from "./WorkOperation";
import { CaseAttachments } from "../case/CaseAttachments";
import { CaseNotes } from "../case/CaseNotes";
import { CaseWork } from "../case/CaseWork";

type Editor = {
  noteIntent?: NoteIntent;
  implicit?: boolean;
  upload?: File;
  action: OperationAffordance;
  subject: "WORK" | "REMINDER" | "NOTE" | "LINK" | "ATTACHMENT";
  kind: string;
  identity: string;
  prefill: FormValues;
  binding?: OperationBinding;
  tasks?: ReadonlyArray<TaskId>;
  anchor?: string;
  origin?: string;
};
const editorOrigin = (next: Editor) =>
  next.origin ?? (document.activeElement instanceof HTMLElement ? document.activeElement.id : null);
export const useCaseWorkPanels = ({
  current,
  onChanged,
  onLockChange,
  disclosureAvailable,
  authoringAvailable = disclosureAvailable,
  attachmentAuthoringAvailable,
}: {
  current: CurrentCase;
  disclosureAvailable: boolean;
  authoringAvailable?: boolean;
  attachmentAuthoringAvailable: boolean;
  onChanged: () => void;
  onLockChange: MutationLockChange;
}) => {
  const noteDraft = useInlineNoteDraft();
  const [editor, setEditor] = useState<Editor | null>(null);
  const [refresh, setRefresh] = useState(0);
  const recorded = useAcceptedOperation();
  const returnFocus = useRef<string | null>(null);
  const focusPending = useRef(false);
  useEffect(() => {
    if (editor === null && focusPending.current) {
      const target =
        returnFocus.current === null ? null : document.getElementById(returnFocus.current);
      (target ?? document.getElementById("case-detail-title"))?.focus();
      focusPending.current = false;
    }
  }, [editor]);
  const target = useMemo(
    () => ({ kind: "ID" as const, caseId: current.subject.caseId }),
    [current.subject.caseId],
  );
  const open = (next: Editor, { anchorToTrigger = true } = {}) => {
    returnFocus.current = editorOrigin(next);
    recorded.clear();
    setEditor({
      ...next,
      ...(anchorToTrigger && returnFocus.current !== null ? { anchor: returnFocus.current } : {}),
    });
  };
  const close = () => {
    setEditor(null);
    focusPending.current = true;
  };
  const accepted = (_receipt: OperationReceipt, operationId: string, noteIntent?: NoteIntent) => {
    if (noteIntent !== undefined) {
      noteDraft.accepted(noteIntent);
    }
    recorded.accept(operationId);
    setRefresh((value) => value + 1);
    setEditor(null);
    focusPending.current = true;
    onChanged();
  };
  const workAction = current.actions.find(
    (action): action is BoundOperationAffordance<"WORK"> =>
      isOperationAffordance(action) &&
      action.task === clientCompositions.assignment &&
      action.binding.kind === "WORK" &&
      action.blocked === null,
  );
  const reminderCreate = current.actions.find(
    (action): action is BoundOperationAffordance<"REMINDER"> =>
      isOperationAffordance(action) &&
      action.task === clientCompositions.reminderCreation &&
      action.binding.kind === "REMINDER" &&
      !("reminderId" in action.binding) &&
      action.blocked === null,
  );
  const noteAdd = current.actions.find(
    (action): action is BoundOperationAffordance<"NOTE"> =>
      isOperationAffordance(action) &&
      isNarrativeCreationId(action.task) &&
      action.task === clientCompositions.noteEntry &&
      action.binding.kind === "NOTE" &&
      !("noteId" in action.binding) &&
      action.blocked === null,
  );
  const work = disclosureAvailable ? (
    <>
      <CaseWork
        canAssign={
          workAction?.editable.some((input) => input === "field:responsibleActor") === true
        }
        canUpdateSummary={
          workAction?.editable.some((input) => input === "field:handlingSummary") === true
        }
        timeZone={current.basis.observation.timeZone}
        {...(current.screen.regions.find((region) => region.role === "WORK") === undefined
          ? {}
          : { region: current.screen.regions.find((region) => region.role === "WORK")! })}
        {...(editor === null && workAction !== undefined
          ? {
              onAssign: (origin: string) => {
                open(
                  {
                    subject: "WORK",
                    action: workAction,
                    kind: workAction.task,
                    identity: "work",
                    origin,
                    prefill: {
                      "field:responsibleActor": workAction.seeds["field:responsibleActor"] ?? "",
                    },
                  },
                  { anchorToTrigger: false },
                );
              },
              onSummary: (origin: string) => {
                open(
                  {
                    subject: "WORK",
                    action: workAction,
                    kind: workAction.task,
                    identity: "work",
                    origin,
                    prefill: {
                      "field:handlingSummary": workAction.seeds["field:handlingSummary"] ?? "",
                    },
                  },
                  { anchorToTrigger: false },
                );
              },
            }
          : {})}
      />
      {recorded.operationId === null ? null : (
        <ReceiptFeedback
          key={recorded.operationId}
          operationId={recorded.operationId}
          dismiss={recorded.clear}
        />
      )}
    </>
  ) : null;
  const onReminder: ReminderActHandler = (action, originId, implicit = false) => {
    const { binding } = action;
    const identity =
      binding.kind === "REMINDER" && "reminderId" in binding ? binding.reminderId : action.task;
    open({
      subject: "REMINDER",
      kind: action.task,
      identity,
      action,
      binding,
      prefill: {},
      implicit,
      ...(originId === undefined ? {} : { origin: originId }),
    });
  };
  // Parent refresh masks the exact read without remounting its refusal-notification latch.
  const nextActions = (
    <>
      <RoutedReminder
        enabled={disclosureAvailable}
        target={target}
        observation={current.basis.observation.observedAt}
        refresh={refresh}
        onAct={onReminder}
        onDisclosureRefused={onChanged}
      />
      {disclosureAvailable ? (
        <CaseNextActions
          current={current}
          canCreate={editor === null && reminderCreate !== undefined}
          onCreate={() => {
            if (reminderCreate !== undefined) {
              open({
                subject: "REMINDER",
                kind: reminderCreate.task,
                identity: "create",
                prefill: {},
                action: reminderCreate,
              });
            }
          }}
          onReminder={onReminder}
        />
      ) : null}
    </>
  );
  const operation =
    editor === null ? null : (
      <WorkOperation
        key={`${editor.subject}:${editor.kind}:${editor.identity}`}
        current={current}
        noteIntent={editor.noteIntent}
        implicit={editor.implicit}
        upload={editor.upload}
        subject={editor.subject}
        kind={editor.kind}
        targetIdentity={editor.identity}
        completionAnchor={editor.anchor}
        prefill={editor.prefill}
        affordance={editor.action}
        binding={editor.binding}
        tasks={editor.tasks}
        onClose={close}
        onRecorded={(receipt, id) => {
          accepted(receipt, id, editor.noteIntent);
        }}
        onLockChange={onLockChange}
        onDisclosureRefused={onChanged}
        disclosureAvailable={disclosureAvailable}
      />
    );
  const notes = (
    <CaseNotes
      refresh={refresh}
      current={current}
      disclosureAvailable={disclosureAvailable}
      authoringAvailable={authoringAvailable}
      editing={editor !== null}
      onAdd={(intent) => {
        const input =
          noteAdd === undefined ? undefined : narrativeCreationInput(operationTask(noteAdd.task));
        if (!disclosureAvailable || noteAdd === undefined || input === undefined) {
          return;
        }
        open({
          subject: "NOTE",
          action: noteAdd,
          kind: noteAdd.task,
          identity: "add",
          noteIntent: intent,
          implicit: true,
          prefill: { [input.id]: intent.text },
        });
      }}
      onRemove={(action) => {
        open({
          subject: "NOTE",
          kind: action.task,
          identity: action.task,
          action,
          binding: action.binding,
          prefill: {},
        });
      }}
      onDisclosureRefused={onChanged}
    />
  );
  const attachments = (
    <CaseAttachments
      key={current.subject.caseId}
      current={current}
      disclosureAvailable={disclosureAvailable}
      authoringAvailable={attachmentAuthoringAvailable}
      editing={editor !== null}
      onAdd={(action, captured) => {
        open(
          {
            subject: "ATTACHMENT",
            kind: action.task,
            identity: "add",
            action,
            binding: action.binding,
            upload: captured.file,
            prefill: {
              "input:attachmentName": captured.file.name,
              "input:attachmentLength": String(captured.file.size),
              "input:attachmentDigest": captured.digest,
            },
          },
          { anchorToTrigger: false },
        );
      }}
      onWithdraw={(action) => {
        open({
          subject: "ATTACHMENT",
          kind: action.task,
          identity: action.task,
          action,
          binding: action.binding,
          prefill: {},
        });
      }}
      onDisclosureChanged={onChanged}
    />
  );
  const onLinkAction = (action: Affordance, originId?: string) => {
    if (!isOperationAffordance(action) || action.binding?.kind !== "LINK") {
      return;
    }
    open({
      subject: "LINK",
      kind: action.task,
      identity: "linkId" in action.binding ? action.binding.linkId : action.task,
      action,
      binding: action.binding,
      prefill: {},
      ...(originId === undefined ? {} : { origin: originId }),
    });
  };
  return {
    work,
    nextActions,
    notes,
    attachments,
    operation,
    onLinkAction,
    editing: editor !== null,
  };
};
