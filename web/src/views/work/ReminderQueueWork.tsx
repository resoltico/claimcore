import { useRef, useState } from "react";
import type { ReminderListRequest, ReminderReadModel, OperationReceipt } from "@contracts/client";
import { useEndpoint } from "../../hooks/useEndpoint";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import type { CurrentCase } from "../../api/service";
import { isCurrentCase } from "../../api/service";
import { renderCatalogKey } from "../../presentation/messages";
import { operationEntry } from "../../protocol/operationCatalog";
import { Dialog } from "../../ui/Dialog";
import { ReminderList } from "./ReminderList";
import { WorkOperation } from "./WorkOperation";

type Selection = { row: ReminderReadModel; kind: string; anchor: string | null; readId: string };
const SelectedReminder = ({
  selection,
  close,
  recorded,
  onLockChange,
  disclosureAvailable,
  refused,
}: {
  selection: Selection;
  close: () => void;
  recorded: (receipt: OperationReceipt) => void;
  onLockChange: (locked: boolean) => void;
  disclosureAvailable: boolean;
  refused: () => void;
}) => {
  const p = usePresentation();
  const { row, kind } = selection;
  const query = useEndpoint(
    "case.get",
    { target: { kind: "ID", caseId: row.reminder.caseId } },
    disclosureAvailable,
    selection.readId,
  );
  const current =
    query.value?.outcome.kind === "SUCCEEDED"
      ? { ...query.value.outcome.data, view: query.value.outcome.view }
      : null;
  const [parent, setParent] = useState<CurrentCase | null>(null);
  if (parent === null && current !== null && isCurrentCase(current)) {
    setParent(current);
  }
  const title = renderCatalogKey(
    p,
    operationEntry({ subject: "REMINDER", kind: `REMINDER_${kind}` }).labelId,
  );
  if (parent === null) {
    return (
      <Dialog
        title={title}
        closeLabel={p.text("ui.close")}
        isOpen
        isDismissable
        onOpenChange={(open) => {
          if (!open) {
            close();
          }
        }}
        placement="end"
      >
        {query.loading ? (
          <p role="status">{p.text("ui.loadingCurrentCase")}</p>
        ) : (
          <p role="alert">
            <NoticeView value={query.message ?? { kind: "local", reason: "incomplete" }} />
          </p>
        )}
      </Dialog>
    );
  }
  return (
    <WorkOperation
      current={parent}
      subject="REMINDER"
      kind={`REMINDER_${kind}`}
      targetIdentity={row.reminder.reminderId}
      {...(kind !== "COMPLETE" || selection.anchor === null
        ? {}
        : { completionAnchor: selection.anchor })}
      prefill={{
        "act.reminderId": row.reminder.reminderId,
        "act.expectedRevision": String(row.reminder.revision),
        ...(kind === "UPDATE"
          ? { "act.action": row.reminder.action, "act.dueDate": row.reminder.dueDate }
          : {}),
      }}
      disclosureAvailable={disclosureAvailable}
      onClose={close}
      onRecorded={recorded}
      onLockChange={onLockChange}
      onDisclosureRefused={refused}
    />
  );
};
/** A queue action owns one selected reminder and one explicit parent read, never per-row reads. */
export const ReminderQueueWork = ({
  filter,
  onLockChange,
  disclosureAvailable,
}: {
  filter: Omit<ReminderListRequest, "target" | "cursor" | "limit">;
  onLockChange: (locked: boolean) => void;
  disclosureAvailable: boolean;
}) => {
  const p = usePresentation();
  const [selection, setSelection] = useState<Selection | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [done, setDone] = useState<string | null>(null);
  const [masked, setMasked] = useState(false);
  const focus = useRef<string | null>(null);
  const close = () => {
    setSelection(null);
    requestAnimationFrame(() => {
      if (focus.current !== null) {
        document.getElementById(focus.current)?.focus();
      }
    });
  };
  return (
    <>
      {done === null ? null : <p role="status">{renderCatalogKey(p, done)}</p>}
      {disclosureAvailable && !masked ? (
        <ReminderList
          key={refresh}
          filter={filter}
          onAct={(row, kind, originId?: string) => {
            focus.current =
              originId ??
              (document.activeElement instanceof HTMLElement ? document.activeElement.id : null);
            setDone(null);
            setSelection({ row, kind, anchor: focus.current, readId: crypto.randomUUID() });
          }}
        />
      ) : null}
      {masked && selection === null ? (
        <button
          type="button"
          onClick={() => {
            setMasked(false);
            setRefresh((value) => value + 1);
          }}
        >
          {p.text("ui.refreshWork")}
        </button>
      ) : null}
      {selection === null ? null : (
        <SelectedReminder
          key={selection.readId}
          selection={selection}
          close={close}
          onLockChange={onLockChange}
          disclosureAvailable={disclosureAvailable}
          refused={() => {
            setMasked(true);
          }}
          recorded={() => {
            setDone(
              operationEntry({ subject: "REMINDER", kind: `REMINDER_${selection.kind}` }).doneId,
            );
            setMasked(false);
            setRefresh((value) => value + 1);
            close();
          }}
        />
      )}
    </>
  );
};
