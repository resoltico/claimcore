import { useMemo, useRef, useState, useEffect } from "react";
import type { OperationReceipt, ReminderReadModel, CaseWorkView } from "@contracts/client";
import type { CurrentCase } from "../../protocol/types";
import type { FormValues } from "../../protocol/forms";
import { usePresentation } from "../../presentation/context";
import { CaseNextActions } from "./CaseNextActions";
import { WorkOperation } from "./WorkOperation";
import { renderCatalogKey } from "../../presentation/messages";
import { operationEntryForInput } from "../../protocol/operationCatalog";
import { CaseWork } from "../case/CaseWork";

type ReminderState = ReminderReadModel["reminder"]["state"];
type Editor = {
  subject: "WORK" | "REMINDER";
  kind: string;
  identity: string;
  prefill: FormValues;
  anchor?: string;
  origin?: string;
};
const reminderPrefill = (row: ReminderReadModel, kind: string): FormValues => ({
  "act.reminderId": row.reminder.reminderId,
  "act.expectedRevision": String(row.reminder.revision),
  ...(kind === "UPDATE"
    ? { "act.action": row.reminder.action, "act.dueDate": row.reminder.dueDate }
    : {}),
});
const workEditOffered = (value: CaseWorkView, editing: boolean): boolean =>
  !editing && (value.canUpdateSummary || value.canAssign);
const reminderCreateOffered = (value: CaseWorkView, editing: boolean): boolean =>
  !editing && value.canCreateReminder;
export const useCaseWorkPanels = ({
  current,
  onChanged,
  onLockChange,
  disclosureAvailable,
}: {
  current: CurrentCase;
  disclosureAvailable: boolean;
  onChanged: () => void;
  onLockChange: (locked: boolean) => void;
}) => {
  const p = usePresentation();
  const [browsing, setBrowsing] = useState(false);
  const [state, setState] = useState<ReminderState>("PENDING");
  const [editor, setEditor] = useState<Editor | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [recorded, setRecorded] = useState<string | null>(null);
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
  const target = useMemo(() => ({ kind: "ID" as const, caseId: current.caseId }), [current.caseId]);
  const filter = useMemo(() => ({ state, includeInactive: true }), [state]);
  const open = (next: Editor) => {
    returnFocus.current =
      next.origin ??
      (document.activeElement instanceof HTMLElement ? document.activeElement.id : null);
    setRecorded(null);
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
  const accepted = (receipt: OperationReceipt) => {
    if (receipt.subject.kind !== "WORK" && receipt.subject.kind !== "REMINDER") {
      return;
    }
    setRecorded(
      operationEntryForInput({ kind: receipt.subject.kind, act: { kind: receipt.subject.act } })
        .doneId,
    );
    setRefresh((value) => value + 1);
    setEditor(null);
    focusPending.current = true;
    onChanged();
  };
  const work = disclosureAvailable ? (
    <>
      <CaseWork
        value={current.work}
        timeZone={current.work.basis.timeZone}
        {...(current.view.regions.find((region) => region.role === "WORK") === undefined
          ? {}
          : { region: current.view.regions.find((region) => region.role === "WORK")! })}
        {...(workEditOffered(current.work, editor !== null)
          ? {
              onAssign: () => {
                open({
                  subject: "WORK",
                  kind: "UPDATE_WORK",
                  identity: "work",
                  prefill: {
                    "act.values.responsibleActor.kind": "SET",
                    "act.values.responsibleActor.value": current.work.responsible?.actorId ?? "",
                  },
                });
              },
              onSummary: () => {
                open({
                  subject: "WORK",
                  kind: "UPDATE_WORK",
                  identity: "work",
                  prefill: { "act.values.handlingSummary": current.work.summary ?? "" },
                });
              },
            }
          : {})}
      />
      {recorded === null ? null : <p role="status">{renderCatalogKey(p, recorded)}</p>}
    </>
  ) : null;
  const onReminder = (row: ReminderReadModel, kind: string, originId?: string) => {
    open({
      subject: "REMINDER",
      kind: `REMINDER_${kind}`,
      identity: row.reminder.reminderId,
      prefill: reminderPrefill(row, kind),
      ...(originId === undefined ? {} : { origin: originId }),
    });
  };
  const nextActions = disclosureAvailable ? (
    <CaseNextActions
      current={current}
      canCreate={reminderCreateOffered(current.work, editor !== null)}
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
        onClose={close}
        onRecorded={accepted}
        onLockChange={onLockChange}
        onDisclosureRefused={onChanged}
        disclosureAvailable={disclosureAvailable}
      />
    );
  return { work, nextActions, operation };
};
