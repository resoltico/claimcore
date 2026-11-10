import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { useRef, useState } from "react";
import { useSessionContext } from "../../session/SessionContext";
import type { ReminderListRequest, OperationTaskId } from "@contracts/client";
import type { OperationReceipt } from "../../protocol/types";
import type { ReminderControl } from "./ReminderActions";
import { operationTask } from "../../protocol/taskCatalog";
import { ScreenView } from "../../ui/ScreenView";
import { useEndpoint } from "../../hooks/useEndpoint";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import type { CurrentCase } from "../../api/service";
import { isCurrentCase } from "../../api/service";
import { renderCatalogKey } from "../../presentation/messages";
import { Dialog } from "../../ui/Dialog";
import { ReminderList } from "./ReminderList";
import { WorkOperation } from "./WorkOperation";

type Selection = {
  row: ReminderControl;
  kind: OperationTaskId;
  anchor: string | null;
  readId: string;
};
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
  onLockChange: MutationLockChange;
  disclosureAvailable: boolean;
  refused: () => void;
}) => {
  const p = usePresentation();
  const { row, kind } = selection;
  const query = useEndpoint(
    "case.get",
    { target: { kind: "ID", caseId: row.caseId } },
    disclosureAvailable,
    selection.readId,
  );
  const current = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.document : null;
  const [parent, setParent] = useState<CurrentCase | null>(null);
  if (parent === null && current !== null && isCurrentCase(current)) {
    setParent(current);
  }
  const task = operationTask(kind);
  const title = renderCatalogKey(p, task.labelId);
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
      kind={task.act}
      targetIdentity={row.reminderId}
      {...(kind !== "REMINDER:REMINDER_COMPLETE" || selection.anchor === null
        ? {}
        : { completionAnchor: selection.anchor })}
      tasks={row.tasks}
      binding={{
        kind: "REMINDER",
        target: { kind: "ID", caseId: row.caseId },
        reminder: { reminderId: row.reminderId, revision: row.revision },
      }}
      prefill={{
        ...(row.actionSeed === null ? {} : { "input:reminderAction": row.actionSeed }),
        ...(row.dueDateSeed === null ? {} : { "input:reminderDueDate": row.dueDateSeed }),
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
type QueueProps = {
  filter: Omit<ReminderListRequest, "target" | "cursor" | "limit">;
  onLockChange: MutationLockChange;
  disclosureAvailable: boolean;
};
const AvailableReminderQueueWork = ({ filter, onLockChange, disclosureAvailable }: QueueProps) => {
  const p = usePresentation();
  const [selection, setSelection] = useState<Selection | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [done, setDone] = useState<OperationReceipt | null>(null);
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
      {done === null || !disclosureAvailable || masked ? null : (
        <ScreenView view={done.screen} timeZone={done.basis.observation.timeZone} />
      )}
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
          recorded={(receipt) => {
            setDone(receipt);
            setMasked(false);
            setRefresh((value) => value + 1);
            close();
          }}
        />
      )}
    </>
  );
};

/** The selected reminder and accepted receipt belong to one authenticated session. */
export const ReminderQueueWork = (props: QueueProps) => {
  const { state } = useSessionContext();
  return state.kind === "authenticated" ? (
    <AvailableReminderQueueWork key={`${state.epoch}:${state.token}`} {...props} />
  ) : null;
};
