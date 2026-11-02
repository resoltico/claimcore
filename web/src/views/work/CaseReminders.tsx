import { useMemo, useRef, useState } from "react";
import type { OperationReceipt, ReminderReadModel, CaseWorkView } from "@contracts/client";
import type { CurrentCase } from "../../protocol/types";
import type { FormValues } from "../../protocol/forms";
import { usePresentation } from "../../presentation/context";
import { ReminderList } from "./ReminderList";
import { WorkOperation } from "./WorkOperation";
import { CaseWork } from "../case/CaseWork";

type ReminderState = ReminderReadModel["reminder"]["state"];
type Editor = { subject: "WORK" | "REMINDER"; kind: string; identity: string; prefill: FormValues };
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
export const CaseReminders = ({
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
  const [state, setState] = useState<ReminderState>("PENDING");
  const [editor, setEditor] = useState<Editor | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [recorded, setRecorded] = useState<"WORK" | "REMINDER" | null>(null);
  const returnFocus = useRef<HTMLElement | null>(null);
  const target = useMemo(() => ({ kind: "ID" as const, caseId: current.caseId }), [current.caseId]);
  const filter = useMemo(() => ({ state, includeInactive: true }), [state]);
  const open = (next: Editor) => {
    returnFocus.current =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setRecorded(null);
    setEditor(next);
  };
  const close = () => {
    setEditor(null);
    returnFocus.current?.focus();
  };
  const accepted = (receipt: OperationReceipt) => {
    if (receipt.subject.kind !== "WORK" && receipt.subject.kind !== "REMINDER") {
      return;
    }
    setRecorded(receipt.subject.kind);
    setRefresh((value) => value + 1);
    setEditor(null);
    onChanged();
  };
  if (!disclosureAvailable && editor === null) {
    return null;
  }
  return (
    <>
      {disclosureAvailable ? (
        <>
          <CaseWork
            value={current.work}
            {...(workEditOffered(current.work, editor !== null)
              ? {
                  onUpdate: () => {
                    open({ subject: "WORK", kind: "UPDATE_WORK", identity: "work", prefill: {} });
                  },
                }
              : {})}
            {...(reminderCreateOffered(current.work, editor !== null)
              ? {
                  onPlan: () => {
                    open({
                      subject: "REMINDER",
                      kind: "REMINDER_CREATE",
                      identity: "create",
                      prefill: {},
                    });
                  },
                }
              : {})}
          />
          {recorded === null ? null : (
            <p role="status">
              {p.text(recorded === "WORK" ? "ui.workRecorded" : "ui.reminderRecorded")}
            </p>
          )}
        </>
      ) : null}
      {editor === null ? (
        <section aria-labelledby="case-reminders-title">
          <h2 id="case-reminders-title">{p.text("ui.reminders")}</h2>
          <label>
            {p.text("ui.reminderState")}
            <select
              value={state}
              onChange={(event) => {
                const { value } = event.target;
                if (value === "PENDING" || value === "COMPLETED" || value === "CANCELLED") {
                  setState(value);
                }
              }}
            >
              {(["PENDING", "COMPLETED", "CANCELLED"] as const).map((value) => (
                <option key={value} value={value}>
                  {p.term("reminderState", value)}
                </option>
              ))}
            </select>
          </label>
          {state === "PENDING" && current.work.pendingReminders === 0 ? (
            <>
              <p>{p.text("ui.noReminders")}</p>
              <button type="button" onClick={onChanged}>
                {p.text("ui.refreshWork")}
              </button>
            </>
          ) : (
            <ReminderList
              key={refresh}
              target={target}
              filter={filter}
              onDisclosureRefused={onChanged}
              onAct={(row, kind) => {
                open({
                  subject: "REMINDER",
                  kind: `REMINDER_${kind}`,
                  identity: row.reminder.reminderId,
                  prefill: reminderPrefill(row, kind),
                });
              }}
            />
          )}
        </section>
      ) : (
        <WorkOperation
          key={`${editor.subject}:${editor.kind}:${editor.identity}`}
          current={current}
          subject={editor.subject}
          kind={editor.kind}
          targetIdentity={editor.identity}
          prefill={editor.prefill}
          onClose={close}
          onRecorded={accepted}
          onLockChange={onLockChange}
          onDisclosureRefused={onChanged}
          disclosureAvailable={disclosureAvailable}
        />
      )}
    </>
  );
};
