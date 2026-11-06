import { Select } from "../../ui/Select";
import { useMemo, useRef, useState, useEffect } from "react";
import type { OperationReceipt, ReminderReadModel, CaseWorkView } from "@contracts/client";
import type { CurrentCase } from "../../protocol/types";
import type { FormValues } from "../../protocol/forms";
import { usePresentation } from "../../presentation/context";
import { ReminderList } from "./ReminderList";
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
        {...(workEditOffered(current.work, editor !== null)
          ? {
              onUpdate: () => {
                open({ subject: "WORK", kind: "UPDATE_WORK", identity: "work", prefill: {} });
              },
            }
          : {})}
      />
      {recorded === null ? null : <p role="status">{renderCatalogKey(p, recorded)}</p>}
    </>
  ) : null;
  const nextActions = disclosureAvailable ? (
    <section className="case-reminders" aria-labelledby="case-reminders-title">
      <div className="section-heading">
        <h2 id="case-reminders-title">{p.text("ui.nextActions")}</h2>
        {reminderCreateOffered(current.work, editor !== null) ? (
          <button
            id="case-reminder-add"
            type="button"
            onClick={() => {
              open({
                subject: "REMINDER",
                kind: "REMINDER_CREATE",
                identity: "create",
                prefill: {},
              });
            }}
          >
            {p.text("ui.newReminder")}
          </button>
        ) : null}
      </div>
      {current.work.needsNextAction ? (
        <p className="work-attention">{p.text("ui.noPendingReminders")}</p>
      ) : null}
      {state === "PENDING" && current.work.pendingReminders === 0 ? (
        <p>{p.text("ui.noReminders")}</p>
      ) : (
        <ReminderList
          key={refresh}
          target={target}
          filter={filter}
          onDisclosureRefused={onChanged}
          onAct={(row, kind, originId?: string) => {
            open({
              subject: "REMINDER",
              kind: `REMINDER_${kind}`,
              identity: row.reminder.reminderId,
              prefill: reminderPrefill(row, kind),
              ...(originId === undefined ? {} : { origin: originId }),
            });
          }}
        />
      )}
      <details className="case-reminder-controls">
        <summary>
          {p.text("ui.reminderState")} · {p.term("reminderState", state)}
        </summary>
        <p className="case-reminder-counts">
          {p.text("ui.pendingReminders")}: {p.integer(current.work.pendingReminders)} ·{" "}
          {p.text("ui.overdueReminders")}: {p.integer(current.work.overdueReminders)}
        </p>
        <Select
          label={p.text("ui.reminderState")}
          value={state}
          options={(["PENDING", "COMPLETED", "CANCELLED"] as const).map((value) => ({
            value,
            label: p.term("reminderState", value),
          }))}
          onChange={(value) => {
            if (value === "PENDING" || value === "COMPLETED" || value === "CANCELLED") {
              setState(value);
            }
          }}
        />
        <button
          type="button"
          onClick={() => {
            setRefresh((value) => value + 1);
            onChanged();
          }}
        >
          {p.text("ui.refreshWork")}
        </button>
      </details>
    </section>
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
