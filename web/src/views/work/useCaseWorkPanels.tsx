import { useAcceptedOperation } from "../../hooks/useAcceptedOperation";
import { ObservedReceipt } from "../operation/ObservedReceipt";
import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { useMemo, useRef, useState, useEffect } from "react";
import type { TaskId } from "@contracts/client";
import type { OperationReceipt, CurrentCase } from "../../protocol/types";
import type { ReminderActHandler } from "./ReminderActions";
import type { TaskBindingSource } from "../../operation/taskForm";
import { operationTask } from "../../protocol/taskCatalog";
import type { FormValues } from "../../protocol/forms";
import { CaseNextActions } from "./CaseNextActions";
import { WorkOperation } from "./WorkOperation";
import { CaseNotes } from "../case/CaseNotes";
import { CaseWork } from "../case/CaseWork";

type ReminderState = "PENDING" | "COMPLETED" | "CANCELLED";
type Editor = {
  subject: "WORK" | "REMINDER" | "NOTE";
  kind: string;
  identity: string;
  prefill: FormValues;
  binding?: TaskBindingSource;
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
    () => ({ kind: "ID" as const, caseId: current.context.caseId }),
    [current.context.caseId],
  );
  const filter = useMemo(() => ({ state, includeInactive: true }), [state]);
  const open = (next: Editor) => {
    returnFocus.current =
      next.origin ??
      (document.activeElement instanceof HTMLElement ? document.activeElement.id : null);
    recorded.clear();
    setEditor({
      ...next,
      ...(next.kind === "REMINDER_COMPLETE" && returnFocus.current !== null
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
  const work = disclosureAvailable ? (
    <>
      <CaseWork
        canAssign={current.context.editableInputs.includes("field:responsibleActor")}
        canUpdateSummary={current.context.editableInputs.includes("field:handlingSummary")}
        timeZone={current.basis.observation.timeZone}
        {...(current.screen.regions.find((region) => region.role === "WORK") === undefined
          ? {}
          : { region: current.screen.regions.find((region) => region.role === "WORK")! })}
        {...(editor === null && current.context.tasks.includes("WORK:UPDATE_WORK")
          ? {
              onAssign: () => {
                open({
                  subject: "WORK",
                  kind: "UPDATE_WORK",
                  identity: "work",
                  prefill: {
                    "input:responsibleActorAction": "SET",
                    "field:responsibleActor": current.context.seeds.work?.responsible ?? "",
                  },
                });
              },
              onSummary: () => {
                open({
                  subject: "WORK",
                  kind: "UPDATE_WORK",
                  identity: "work",
                  prefill: { "field:handlingSummary": current.context.seeds.work?.summary ?? "" },
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
  const onReminder: ReminderActHandler = (row, taskId, originId) => {
    open({
      subject: "REMINDER",
      kind: operationTask(taskId).act,
      identity: row.reminderId,
      binding: {
        kind: "REMINDER",
        target,
        reminder: { reminderId: row.reminderId, revision: row.revision },
      },
      tasks: row.tasks,
      prefill: {
        ...(row.actionSeed === null ? {} : { "input:reminderAction": row.actionSeed }),
        ...(row.dueDateSeed === null ? {} : { "input:reminderDueDate": row.dueDateSeed }),
      },
      ...(originId === undefined ? {} : { origin: originId }),
    });
  };
  const nextActions = disclosureAvailable ? (
    <CaseNextActions
      current={current}
      canCreate={editor === null && current.context.tasks.includes("REMINDER:REMINDER_CREATE")}
      onCreate={() => {
        open({ subject: "REMINDER", kind: "REMINDER_CREATE", identity: "create", prefill: {} });
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
        {...(editor.anchor === undefined ? {} : { completionAnchor: editor.anchor })}
        prefill={editor.prefill}
        {...(editor.binding === undefined ? {} : { binding: editor.binding })}
        {...(editor.tasks === undefined ? {} : { tasks: editor.tasks })}
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
        open({
          subject: "NOTE",
          kind: "ADD_NOTE",
          identity: "add",
          prefill: { "input:noteText": text },
        });
      }}
      onRemove={(id) => {
        open({
          subject: "NOTE",
          kind: "REMOVE_NOTE",
          identity: id,
          prefill: {},
          binding: { kind: "NOTE", target, noteId: id },
        });
      }}
      onDisclosureRefused={onChanged}
    />
  ) : null;
  return { work, nextActions, notes, operation };
};
