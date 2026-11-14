import { useAcceptedOperation } from "../../hooks/useAcceptedOperation";
import { ObservedReceipt } from "../operation/ObservedReceipt";
import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { useMemo, useRef, useState, useEffect } from "react";
import type { TaskId, OperationBinding } from "@contracts/client";
import type { OperationReceipt, CurrentCase } from "../../protocol/types";
import type { ReminderActHandler } from "./reminderActions";

import type { OperationAffordance } from "../../operation/affordances";

import type { FormValues } from "../../protocol/forms";
import { CaseNextActions } from "./CaseNextActions";
import { WorkOperation } from "./WorkOperation";
import { CaseNotes } from "../case/CaseNotes";
import { CaseWork } from "../case/CaseWork";

type ReminderState = "PENDING" | "COMPLETED" | "CANCELLED";
type Editor = {
  action: OperationAffordance;
  subject: "WORK" | "REMINDER" | "NOTE";
  kind: string;
  identity: string;
  prefill: FormValues;
  binding?: OperationBinding;
  tasks?: ReadonlyArray<TaskId>;
  anchor?: string;
  origin?: string;
};
export const useCaseWorkPanels = ({
  current,
  onChanged,
  onLockChange,
  disclosureAvailable,
}: {
  current: CurrentCase;
  disclosureAvailable: boolean;
  onChanged: () => void;
  onLockChange: MutationLockChange;
}) => {
  const [browsing, setBrowsing] = useState(false);
  const [state, setState] = useState<ReminderState>("PENDING");
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
  const filter = useMemo(() => ({ state, includeInactive: true }), [state]);
  const open = (next: Editor) => {
    returnFocus.current =
      next.origin ??
      (document.activeElement instanceof HTMLElement ? document.activeElement.id : null);
    recorded.clear();
    setEditor({
      ...next,
      ...(next.kind === "reminder.complete" && returnFocus.current !== null
        ? { anchor: returnFocus.current }
        : {}),
    });
  };
  const close = () => {
    setEditor(null);
    focusPending.current = true;
  };
  const accepted = (_receipt: OperationReceipt, operationId: string) => {
    recorded.accept(operationId);
    setRefresh((value) => value + 1);
    setEditor(null);
    focusPending.current = true;
    onChanged();
  };
  const workAction = current.actions.find(
    (action): action is Extract<OperationAffordance, { task: "work.update" }> =>
      action.task === "work.update" && action.blocked === null,
  );
  const reminderCreate = current.actions.find(
    (action): action is Extract<OperationAffordance, { task: "reminder.create" }> =>
      action.task === "reminder.create" && action.blocked === null,
  );
  const noteAdd = current.actions.find(
    (action): action is Extract<OperationAffordance, { task: "note.add" }> =>
      action.task === "note.add" && action.blocked === null,
  );
  const work = disclosureAvailable ? (
    <>
      <CaseWork
        canAssign={workAction?.editable.includes("field:responsibleActor") === true}
        canUpdateSummary={workAction?.editable.includes("field:handlingSummary") === true}
        timeZone={current.basis.observation.timeZone}
        {...(current.screen.regions.find((region) => region.role === "WORK") === undefined
          ? {}
          : { region: current.screen.regions.find((region) => region.role === "WORK")! })}
        {...(editor === null && workAction !== undefined
          ? {
              onAssign: () => {
                open({
                  subject: "WORK",
                  action: workAction,
                  kind: "work.update",
                  identity: "work",
                  prefill: {
                    "field:responsibleActor": workAction.seeds["field:responsibleActor"] ?? "",
                  },
                });
              },
              onSummary: () => {
                open({
                  subject: "WORK",
                  action: workAction,
                  kind: "work.update",
                  identity: "work",
                  prefill: {
                    "field:handlingSummary": workAction.seeds["field:handlingSummary"] ?? "",
                  },
                });
              },
            }
          : {})}
      />
      {recorded.operationId === null ? null : (
        <ObservedReceipt key={recorded.operationId} operationId={recorded.operationId} />
      )}
    </>
  ) : null;
  const onReminder: ReminderActHandler = (action, originId) => {
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
      ...(originId === undefined ? {} : { origin: originId }),
    });
  };
  const nextActions = disclosureAvailable ? (
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
      state={state}
      setState={setState}
      browsing={browsing}
      setBrowsing={setBrowsing}
      refresh={refresh}
      target={target}
      filter={filter}
      onChanged={onChanged}
      onRefresh={() => {
        setRefresh((value) => value + 1);
        onChanged();
      }}
    />
  ) : null;
  const operation =
    editor === null ? null : (
      <WorkOperation
        key={`${editor.subject}:${editor.kind}:${editor.identity}`}
        current={current}
        subject={editor.subject}
        kind={editor.kind}
        targetIdentity={editor.identity}
        completionAnchor={editor.anchor}
        prefill={editor.prefill}
        affordance={editor.action}
        binding={editor.binding}
        tasks={editor.tasks}
        onClose={close}
        onRecorded={accepted}
        onLockChange={onLockChange}
        onDisclosureRefused={onChanged}
        disclosureAvailable={disclosureAvailable}
      />
    );
  const notes = disclosureAvailable ? (
    <CaseNotes
      key={refresh}
      current={current}
      editing={editor !== null}
      onAdd={(text) => {
        if (noteAdd === undefined) {
          return;
        }
        open({
          subject: "NOTE",
          action: noteAdd,
          kind: "note.add",
          identity: "add",
          prefill: { "input:noteText": text },
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
  ) : null;
  return { work, nextActions, notes, operation };
};
